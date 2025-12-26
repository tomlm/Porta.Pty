/*
 * porta_pty.c - Native PTY shim for Porta.Pty
 * 
 * This native library wraps forkpty() + execvp() to avoid W^X (Write XOR Execute)
 * memory protection issues when forking from managed .NET code on .NET 7+.
 * 
 * By performing fork+exec entirely in native code, we avoid running any managed
 * code in the forked child process.
 * 
 * Copyright (c) Microsoft Corporation. All rights reserved.
 * Licensed under the MIT license.
 */

#if defined(__APPLE__)
    #include <util.h>
    #include <sys/ioctl.h>
#else
    #include <pty.h>
#endif

#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <errno.h>
#include <termios.h>
#include <sys/wait.h>
#include <signal.h>

/* Export macro for shared library symbols */
#if defined(_WIN32)
    #define PTY_EXPORT __declspec(dllexport)
#else
    #define PTY_EXPORT __attribute__((visibility("default")))
#endif

/*
 * Structure to pass terminal settings to the native spawn function.
 * This mirrors the managed Termios structure.
 */
typedef struct {
    unsigned int c_iflag;      /* input modes */
    unsigned int c_oflag;      /* output modes */
    unsigned int c_cflag;      /* control modes */
    unsigned int c_lflag;      /* local modes */
    unsigned char c_cc[32];    /* control characters (NCCS is typically 20-32) */
    unsigned int c_ispeed;     /* input speed */
    unsigned int c_ospeed;     /* output speed */
} pty_termios_t;

/*
 * Structure to pass window size to the native spawn function.
 */
typedef struct {
    unsigned short ws_row;     /* rows, in characters */
    unsigned short ws_col;     /* columns, in characters */
    unsigned short ws_xpixel;  /* horizontal size, pixels (unused) */
    unsigned short ws_ypixel;  /* vertical size, pixels (unused) */
} pty_winsize_t;

/*
 * Result structure returned by pty_spawn.
 */
typedef struct {
    int master_fd;             /* PTY master file descriptor */
    int pid;                   /* Child process ID, or -1 on error */
    int error;                 /* errno value if pid == -1 */
} pty_spawn_result_t;

/*
 * Spawns a new process with a pseudo-terminal.
 * 
 * This function performs forkpty() + execvp() entirely in native code,
 * avoiding W^X issues when called from .NET 7+.
 * 
 * Parameters:
 *   file        - The executable to run (searched in PATH)
 *   argv        - NULL-terminated array of arguments (argv[0] should be the program name)
 *   envp        - NULL-terminated array of environment variables ("KEY=VALUE" format),
 *                 or NULL to inherit the parent's environment
 *   working_dir - Working directory for the child process, or NULL to inherit
 *   termios     - Terminal settings, or NULL for defaults
 *   winsize     - Window size, or NULL for defaults
 * 
 * Returns:
 *   pty_spawn_result_t with master_fd and pid on success, or pid=-1 and error set on failure
 */
PTY_EXPORT pty_spawn_result_t pty_spawn(
    const char* file,
    char* const argv[],
    char* const envp[],
    const char* working_dir,
    const pty_termios_t* termios_settings,
    const pty_winsize_t* winsize_settings)
{
    pty_spawn_result_t result = { -1, -1, 0 };
    
    /* Set up termios structure */
    struct termios term;
    struct termios* term_ptr = NULL;
    
    if (termios_settings != NULL) {
        memset(&term, 0, sizeof(term));
        term.c_iflag = termios_settings->c_iflag;
        term.c_oflag = termios_settings->c_oflag;
        term.c_cflag = termios_settings->c_cflag;
        term.c_lflag = termios_settings->c_lflag;
        
        /* Copy control characters (use minimum of both sizes) */
        size_t cc_size = sizeof(term.c_cc);
        if (cc_size > 32) cc_size = 32;
        memcpy(term.c_cc, termios_settings->c_cc, cc_size);
        
        cfsetispeed(&term, termios_settings->c_ispeed);
        cfsetospeed(&term, termios_settings->c_ospeed);
        
        term_ptr = &term;
    }
    
    /* Set up winsize structure */
    struct winsize ws;
    struct winsize* ws_ptr = NULL;
    
    if (winsize_settings != NULL) {
        ws.ws_row = winsize_settings->ws_row;
        ws.ws_col = winsize_settings->ws_col;
        ws.ws_xpixel = winsize_settings->ws_xpixel;
        ws.ws_ypixel = winsize_settings->ws_ypixel;
        ws_ptr = &ws;
    }
    
    /* Fork with PTY */
    int master_fd = -1;
    pid_t pid = forkpty(&master_fd, NULL, term_ptr, ws_ptr);
    
    if (pid == -1) {
        /* forkpty failed */
        result.error = errno;
        return result;
    }
    
    if (pid == 0) {
        /* 
         * Child process - NO MANAGED CODE RUNS HERE!
         * This is the key to avoiding W^X issues.
         */
        
        /* Change working directory if specified */
        if (working_dir != NULL && working_dir[0] != '\0') {
            if (chdir(working_dir) == -1) {
                _exit(errno);
            }
        }
        
        /* Set TERM environment variable if not already set */
        if (getenv("TERM") == NULL) {
            setenv("TERM", "xterm-256color", 0);
        }
        
        /* Apply custom environment variables if provided */
        if (envp != NULL) {
            for (int i = 0; envp[i] != NULL; i++) {
                /* Parse "KEY=VALUE" format */
                char* eq = strchr(envp[i], '=');
                if (eq != NULL) {
                    size_t key_len = eq - envp[i];
                    char* key = (char*)alloca(key_len + 1);
                    memcpy(key, envp[i], key_len);
                    key[key_len] = '\0';
                    
                    const char* value = eq + 1;
                    
                    if (value[0] == '\0') {
                        /* Empty value means unset */
                        unsetenv(key);
                    } else {
                        setenv(key, value, 1);
                    }
                }
            }
        }
        
        /* Execute the program */
        execvp(file, argv);
        
        /* If we get here, execvp failed */
        _exit(errno);
    }
    
    /* Parent process */
    result.master_fd = master_fd;
    result.pid = pid;
    result.error = 0;
    
    return result;
}

/*
 * Resizes the PTY window.
 * 
 * Parameters:
 *   master_fd - The PTY master file descriptor
 *   rows      - New number of rows
 *   cols      - New number of columns
 * 
 * Returns:
 *   0 on success, -1 on failure (check errno)
 */
PTY_EXPORT int pty_resize(int master_fd, unsigned short rows, unsigned short cols)
{
    struct winsize ws;
    ws.ws_row = rows;
    ws.ws_col = cols;
    ws.ws_xpixel = 0;
    ws.ws_ypixel = 0;
    
    return ioctl(master_fd, TIOCSWINSZ, &ws);
}

/*
 * Sends a signal to the child process.
 * 
 * Parameters:
 *   pid    - The child process ID
 *   signal - The signal to send (e.g., SIGHUP, SIGTERM, SIGKILL)
 * 
 * Returns:
 *   0 on success, -1 on failure (check errno)
 */
PTY_EXPORT int pty_kill(int pid, int signal)
{
    return kill(pid, signal);
}

/*
 * Waits for the child process to exit.
 * 
 * Parameters:
 *   pid     - The child process ID
 *   status  - Pointer to store the exit status
 *   options - waitpid options (0 for blocking, WNOHANG for non-blocking)
 * 
 * Returns:
 *   The PID on success, 0 if WNOHANG and child hasn't exited, -1 on failure
 */
PTY_EXPORT int pty_waitpid(int pid, int* status, int options)
{
    return waitpid(pid, status, options);
}

/*
 * Closes the PTY master file descriptor.
 * 
 * Parameters:
 *   master_fd - The PTY master file descriptor
 * 
 * Returns:
 *   0 on success, -1 on failure
 */
PTY_EXPORT int pty_close(int master_fd)
{
    return close(master_fd);
}

/*
 * Gets the last error code.
 * Useful for debugging when functions return -1.
 * 
 * Returns:
 *   The current errno value
 */
PTY_EXPORT int pty_get_errno(void)
{
    return errno;
}
