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
    #include <sys/event.h>
    #include <sys/time.h>
#else
    #include <pty.h>
    #include <sys/epoll.h>
    #include <sys/syscall.h>
#endif

#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <pthread.h>
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
/*
 * Serialises pty allocation.
 *
 * Darwin's forkpty() -> openpty() -> grantpt()/unlockpt() path is NOT thread safe. Called
 * concurrently it fails outright, and often: a pure-C harness with 24 threads calling pty_spawn at
 * once failed 5 of 24 on three consecutive runs, and 0 of 24 on three consecutive runs with nothing
 * changed but this lock. No .NET involved in either.
 *
 * The failure is nasty to diagnose because it does not report a usable errno. forkpty returns -1 and
 * leaves errno at -6 -- NEGATIVE, so it is not an errno at all but a kernel-style -ENXIO leaking out
 * of the pty allocator. Anything that prints strerror(errno) says "Undefined error: 0", and anything
 * that reads it as a POSIX errno concludes something false. It is also easy to misread as fd
 * exhaustion: measured during a failing run, the process held 30 open descriptors against a limit of
 * 1048576, and the system had 31 of 511 ptys in use. Nothing was exhausted.
 *
 * This is not a test-only concern: opening several terminals at once on macOS failed about one
 * time in five.
 *
 * The lock is held across fork(), which is safe HERE for the narrow reason that the child never
 * touches it: on every path the child either execvp()s or _exit()s, so its copy of the mutex dies
 * with it. Only the parent unlocks, hence the pid != 0 test.
 *
 * Linux's glibc openpty does not appear to need this, and the lock costs microseconds on a path that
 * is already forking a process, so it is applied unconditionally rather than ifdef'd per platform.
 */
static pthread_mutex_t pty_spawn_lock = PTHREAD_MUTEX_INITIALIZER;

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
    pthread_mutex_lock(&pty_spawn_lock);
    pid_t pid = forkpty(&master_fd, NULL, term_ptr, ws_ptr);
    int spawn_errno = errno;
    if (pid != 0) {
        /* Parent, or forkpty failed. The child must not unlock a mutex it only has a copy of. */
        pthread_mutex_unlock(&pty_spawn_lock);
    }
    
    if (pid == -1) {
        /* forkpty failed */
        result.error = spawn_errno;
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
/*
 * Puts the pty controller into non-blocking mode.
 *
 * This exists in the shim rather than as a libc P/Invoke because fcntl is VARIADIC --
 * int fcntl(int, int, ...) -- and on Apple ARM64 variadic arguments are passed on the stack
 * while fixed arguments are passed in registers. A P/Invoke declaring three fixed ints puts
 * the third in a register, the callee reads the stack, and F_SETFL applies whatever junk was
 * there. It then returns 0, so the caller is told the descriptor is non-blocking when it is
 * not. Observed on macOS: asking for O_RDWR|O_NONBLOCK (0x6) produced flags of 0x400042.
 *
 * It happens to work on Linux, where both calling conventions pass integers in registers, so
 * the failure is macOS-only and silent -- which is worse than a failure that is neither.
 */
PTY_EXPORT int pty_set_nonblocking(int fd)
{
    int flags = fcntl(fd, F_GETFL, 0);
    if (flags == -1)
    {
        return -1;
    }

    return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

/*
 * Exit notification without polling.
 *
 * The reaper polls waitpid(WNOHANG) every 100ms, which costs latency on WaitForExit and
 * ProcessExited and a thread to do it on. Both kernels can tell us instead, and both can do it
 * through a single POLLABLE descriptor -- which means the existing pty poll(2) loop can carry it and
 * the reaper thread stops existing.
 *
 * The two mechanisms are shaped differently and are wrapped into one here:
 *   macOS  one kqueue holds an EVFILT_PROC/NOTE_EXIT registration per pid.
 *   Linux  one epoll holds a pidfd per pid. pidfd_open needs Linux 5.3, and had no glibc wrapper
 *          before 2.36, so it goes through syscall() -- which is variadic, and therefore exactly
 *          the thing that must not be called from a P/Invoke. See pty_set_nonblocking.
 *
 * pty_exit_queue returns -1 when this kernel cannot do it, which the caller reports as an
 * unsupported platform rather than silently doing something slower.
 */
PTY_EXPORT int pty_exit_queue(void)
{
#if defined(__APPLE__)
    return kqueue();
#elif defined(SYS_pidfd_open)
    /*
     * PROBE pidfd_open, do not merely compile against it. SYS_pidfd_open is a property of the
     * build machine's headers, and epoll_create1 succeeds on every kernel -- so testing either one
     * says nothing about whether the kernel running this binary can do the thing. Built on a
     * modern CI image and run on a 4.18 kernel, both tests pass and every syscall that matters
     * then fails at the point of use.
     *
     * Opening a descriptor to ourselves answers the question directly and costs one syscall, once.
     */
    int probe = (int)syscall(SYS_pidfd_open, getpid(), 0);
    if (probe == -1)
    {
        return -1;
    }
    close(probe);

    return epoll_create1(EPOLL_CLOEXEC);
#else
    errno = ENOSYS;
    return -1;
#endif
}

/*
 * Starts watching one pid. Returns 0 on success and -1 otherwise -- including when the child has
 * ALREADY exited, which both kernels report as ESRCH. That case is not a failure either; the caller
 * reaps it directly. Losing that distinction would lose the exit.
 */
PTY_EXPORT int pty_exit_watch(int queue, int pid)
{
#if defined(__APPLE__)
    struct kevent kev;
    EV_SET(&kev, (uintptr_t)pid, EVFILT_PROC, EV_ADD | EV_ONESHOT, NOTE_EXIT, 0, NULL);
    return kevent(queue, &kev, 1, NULL, 0, NULL);
#elif defined(SYS_pidfd_open)
    int pidfd = (int)syscall(SYS_pidfd_open, pid, 0);
    if (pidfd == -1)
    {
        return -1;
    }

    struct epoll_event ev;
    ev.events = EPOLLIN;
    /* Both halves are needed on the way out: the pid to reap, the descriptor to close. */
    ev.data.u64 = ((uint64_t)(uint32_t)pidfd << 32) | (uint32_t)pid;
    if (epoll_ctl(queue, EPOLL_CTL_ADD, pidfd, &ev) == -1)
    {
        int saved = errno;
        close(pidfd);
        errno = saved;
        return -1;
    }

    return 0;
#else
    (void)queue; (void)pid;
    errno = ENOSYS;
    return -1;
#endif
}

/*
 * Collects the pids that have exited, without blocking. Returns how many were written, or -1.
 * The caller still has to waitpid() each one: these kernels report the exit, not the status.
 */
PTY_EXPORT int pty_exit_drain(int queue, int *pids, int max)
{
    if (max <= 0)
    {
        return 0;
    }

#if defined(__APPLE__)
    struct kevent events[64];
    int want = max < 64 ? max : 64;
    struct timespec zero = {0, 0};
    int n = kevent(queue, NULL, 0, events, want, &zero);
    if (n == -1)
    {
        /* Reported as a failure, not as "nothing exited". Falling through returned 0, which the
         * caller cannot tell from an empty drain -- and the Linux branch below returns -1 for the
         * same condition, so the two platforms disagreed with each other and with this function's
         * own contract. */
        return -1;
    }

    int out = 0;
    for (int i = 0; i < n; i++)
    {
        /*
         * An error entry carries EV_ERROR in flags, the errno in data, and the pid it failed for in
         * ident -- so without this it would be reported as that pid having exited. Registration
         * errors come back through pty_exit_watch's return value instead, because it passes
         * nevents = 0, so this is defensive rather than a known path. It costs two lines.
         */
        if (events[i].flags & EV_ERROR)
        {
            continue;
        }

        pids[out++] = (int)events[i].ident;
    }
    return out;
#elif defined(SYS_pidfd_open)
    struct epoll_event events[64];
    int want = max < 64 ? max : 64;
    int n = epoll_wait(queue, events, want, 0);
    for (int i = 0; i < n; i++)
    {
        int pidfd = (int)(events[i].data.u64 >> 32);
        pids[i] = (int)(uint32_t)events[i].data.u64;
        /* One shot, to match the macOS registration: drop it and close the descriptor. */
        epoll_ctl(queue, EPOLL_CTL_DEL, pidfd, NULL);
        close(pidfd);
    }
    return n;
#else
    (void)queue; (void)pids;
    errno = ENOSYS;
    return -1;
#endif
}

PTY_EXPORT int pty_get_errno(void)
{
    return errno;
}
