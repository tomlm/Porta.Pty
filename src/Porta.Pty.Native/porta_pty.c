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
    #include <crt_externs.h>
    #include <sys/ioctl.h>
    #include <sys/proc.h>
    #include <sys/sysctl.h>
    #include <sys/event.h>
    #include <sys/time.h>
    #include <libproc.h>
#else
    #include <pty.h>
    #include <dirent.h>
    #include <stdio.h>
    #include <sys/epoll.h>
    #include <sys/syscall.h>

/*
 * pidfd_open's syscall number, when the build machine's headers are too old to name it. The shim is
 * built on glibc 2.28 so it loads on RHEL 8, and those headers predate pidfd_open -- gating on
 * SYS_pidfd_open being DEFINED would compile the feature out for every target. Support is a runtime
 * question, and pty_exit_queue probes for it.
 *
 * 434 on x86_64 and aarch64, the only Linux architectures shipped; not defined elsewhere, because
 * the number is not universal and guessing is worse than the ENOSYS below.
 */
#if !defined(SYS_pidfd_open) && (defined(__x86_64__) || defined(__aarch64__))
    #define SYS_pidfd_open 434
#endif
#if !defined(SYS_close_range) && (defined(__x86_64__) || defined(__aarch64__))
    #define SYS_close_range 436
#endif
#endif
#include <sys/resource.h>

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

/*
 * Keeps every descriptor above stderr from reaching the program: the forked child closes them before
 * exec on Linux, and marks them close-on-exec on macOS.
 *
 * forkpty does not mark the controller close-on-exec, and descriptors the host made without
 * O_CLOEXEC (the poller's wakeup pipe, other terminals' controllers) were inherited by every child:
 * terminal B could write into terminal A's controller. Only async-signal-safe calls are used here,
 * since this runs between fork and exec of a multithreaded host.
 *
 * macOS marks instead of closing, as Crashpad does: close() of a descriptor with a close guard
 * (guarded_open_np, change_fdguard_np) raises EXC_GUARD and kills the child, while setting
 * close-on-exec is allowed, and exec then closes the descriptors itself.
 */
static void pty_close_inherited_fds(void)
{
#if defined(__APPLE__)
    struct proc_fdinfo fds[512];
    int bytes = proc_pidinfo(getpid(), PROC_PIDLISTFDS, 0, fds, (int)sizeof(fds));
    if (bytes > 0) {
        int n = bytes / (int)sizeof(fds[0]);
        for (int i = 0; i < n; i++) {
            if (fds[i].proc_fd > 2) {
                fcntl(fds[i].proc_fd, F_SETFD, FD_CLOEXEC);
            }
        }
        /* A marked descriptor stays in the listing, so listing again cannot show what is left. A full
         * buffer may mean more remain, and only then does the walk below run. */
        if (bytes < (int)sizeof(fds)) {
            return;
        }
    }
#else
#if defined(SYS_close_range)
    if (syscall(SYS_close_range, 3U, ~0U, 0U) == 0) {
        return;
    }
#endif
    int dir = open("/proc/self/fd", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    if (dir >= 0) {
        char buf[4096];
        long n;
        int restart = 1;
        while (restart) {
            restart = 0;
            lseek(dir, 0, SEEK_SET);
            while ((n = syscall(SYS_getdents64, dir, buf, sizeof(buf))) > 0) {
                for (long off = 0; off < n;) {
                    unsigned short reclen = *(unsigned short*)(buf + off + 16);
                    const char* name = buf + off + 19;
                    off += reclen;
                    if (name[0] < '0' || name[0] > '9') {
                        continue;
                    }
                    int fd = 0;
                    for (const char* c = name; *c >= '0' && *c <= '9'; c++) {
                        fd = fd * 10 + (*c - '0');
                    }
                    if (fd > 2 && fd != dir) {
                        close(fd);
                        restart = 1;
                    }
                }
            }
        }
        close(dir);
        return;
    }
#endif
    /* Walk the whole table: the last resort, and on macOS what follows a full listing. */
    struct rlimit rl;
    long max = (getrlimit(RLIMIT_NOFILE, &rl) == 0 && rl.rlim_cur != RLIM_INFINITY) ? (long)rl.rlim_cur : 65536;
    for (long fd = 3; fd < max; fd++) {
#if defined(__APPLE__)
        fcntl((int)fd, F_SETFD, FD_CLOEXEC);
#else
        close((int)fd);
#endif
    }
}

/*
 * pty_spawn_ex flags.
 *
 * PTY_SPAWN_CLEAR_ENV: the child's environment is EXACTLY envp (NULL meaning none at all) instead of
 * the parent's with envp applied on top. Without it a variable cannot be withheld: the forked child
 * starts from a copy of the parent's environ, so leaving a key out of envp still inherits it. Values
 * are taken as given, so "KEY=" sets KEY to the empty string, and TERM is not defaulted.
 */
#define PTY_SPAWN_CLEAR_ENV 0x1

#if !defined(__APPLE__)
extern char** environ;
#endif

PTY_EXPORT pty_spawn_result_t pty_spawn_ex(
    const char* file,
    char* const argv[],
    char* const envp[],
    const char* working_dir,
    const pty_termios_t* termios_settings,
    const pty_winsize_t* winsize_settings,
    int flags);

PTY_EXPORT pty_spawn_result_t pty_spawn(
    const char* file,
    char* const argv[],
    char* const envp[],
    const char* working_dir,
    const pty_termios_t* termios_settings,
    const pty_winsize_t* winsize_settings)
{
    return pty_spawn_ex(file, argv, envp, working_dir, termios_settings, winsize_settings, 0);
}

PTY_EXPORT pty_spawn_result_t pty_spawn_ex(
    const char* file,
    char* const argv[],
    char* const envp[],
    const char* working_dir,
    const pty_termios_t* termios_settings,
    const pty_winsize_t* winsize_settings,
    int flags)
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
    
    /*
     * Fork with PTY — with every signal BLOCKED across the fork, and the child resetting every
     * disposition before it execs. Why this matters, found the hard way (a CI test host dying on a fast box,
     * 2026-09-07, kernel signal tracepoints):
     *
     * The child is a memory copy of the .NET host, INCLUDING the CoreCLR signal handlers. CoreCLR's
     * SIGTERM/SIGINT/SIGQUIT handlers re-raise the signal with a process id cached at runtime start
     * (kill(gPID, sig) in coreclr/pal/src/exception/signal.cpp) — which, in a forked child, is the
     * PARENT's pid. So a caller that kills the child inside the fork->exec window (a Stop issued
     * right after Start; the window is ~6ms on a fast box) does not kill the child: the child's
     * inherited handler forwards the SIGTERM to the process that spawned it, and THAT dies.
     *
     * exec resets caught signals to SIG_DFL, but only once it happens. Blocking first means a signal
     * sent in the window stays pending until the child has installed SIG_DFL and restored the mask,
     * at which point it takes its default action on the child — which is what the sender meant.
     * This is what .NET's own ForkAndExecProcess does before exec, for the same reason.
     */
    int master_fd = -1;
    sigset_t all_signals, saved_mask;
    sigfillset(&all_signals);
    int mask_err = pthread_sigmask(SIG_BLOCK, &all_signals, &saved_mask);
    if (mask_err != 0) {
        /* saved_mask is unspecified on failure; spawning with an unknown mask is worse than not spawning. */
        result.error = mask_err;
        return result;
    }
    pthread_mutex_lock(&pty_spawn_lock);
    pid_t pid = forkpty(&master_fd, NULL, term_ptr, ws_ptr);
    int spawn_errno = errno;
    if (pid > 0) {
        /* Still under the lock, so no other terminal's child can inherit it from here on. */
        fcntl(master_fd, F_SETFD, FD_CLOEXEC);
    }
    if (pid != 0) {
        /* Parent, or forkpty failed. The child must not unlock a mutex it only has a copy of. */
        pthread_mutex_unlock(&pty_spawn_lock);
        pthread_sigmask(SIG_SETMASK, &saved_mask, NULL);
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
        
        /* Drop the parent's signal handlers BEFORE anything can be delivered (see the note above
         * forkpty). sigaction is async-signal-safe; SIGKILL/SIGSTOP refuse with EINVAL, harmlessly. */
        struct sigaction dfl;
        memset(&dfl, 0, sizeof(dfl));
        dfl.sa_handler = SIG_DFL;
        sigemptyset(&dfl.sa_mask);
        for (int sig = 1; sig < NSIG; sig++) {
            sigaction(sig, &dfl, NULL);
        }
        pthread_sigmask(SIG_SETMASK, &saved_mask, NULL);

        pty_close_inherited_fds();

        /* Change working directory if specified */
        if (working_dir != NULL && working_dir[0] != '\0') {
            if (chdir(working_dir) == -1) {
                _exit(errno);
            }
        }
        
        if (flags & PTY_SPAWN_CLEAR_ENV) {
            /* Point environ at envp itself: no allocation between fork and exec, and execvp then both
             * searches the caller's PATH and passes exactly these strings on. The array lives in the
             * parent's memory, which the child has a copy of. */
            static char* const empty_env[] = { NULL };
            char** env = (char**)(envp != NULL ? envp : empty_env);
#if defined(__APPLE__)
            *_NSGetEnviron() = env;
#else
            environ = env;
#endif
            execvp(file, argv);
            _exit(errno);
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
 * waitpid every 100ms cost latency on WaitForExit and a thread to poll on. Both kernels report an
 * exit through a POLLABLE descriptor instead, so the pty poll(2) loop carries it and the reaper
 * thread stops existing: one kqueue holding EVFILT_PROC/NOTE_EXIT per pid on macOS, one epoll
 * holding a pidfd per pid on Linux.
 *
 * pty_exit_queue returns -1 when the kernel cannot do it, which the caller reports as unsupported
 * rather than silently doing something slower.
 */
PTY_EXPORT int pty_exit_queue(void)
{
#if defined(__APPLE__)
    return kqueue();
#elif defined(SYS_pidfd_open)
    /*
     * PROBE, do not merely compile against it: epoll_create1 succeeds on every kernel and
     * SYS_pidfd_open describes the build machine, so neither says whether THIS kernel can do it.
     * One syscall, once.
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

/*
 * Waits until pid has exited WITHOUT collecting it (WNOWAIT), so the caller can collect it under its
 * own lock. Until it is collected the pid stays a zombie and cannot be handed to another process.
 */
PTY_EXPORT int pty_wait_exit_noreap(int pid)
{
    siginfo_t info;
    int r;
    do {
        memset(&info, 0, sizeof(info));
        r = waitid(P_PID, (id_t)pid, &info, WEXITED | WNOWAIT);
    } while (r == -1 && errno == EINTR);
    return r;
}

/*
 * Whether pid has exited, WITHOUT waiting and without collecting it: 1 if it has, 0 if not, -1 on
 * failure. A stop or continue report is "not exited": Darwin's waitid reports a stopped child even
 * when only WEXITED is asked for, so the code is checked rather than trusting the call.
 */
PTY_EXPORT int pty_exited_noreap(int pid)
{
    siginfo_t info;
    int r;
    do {
        memset(&info, 0, sizeof(info));
        r = waitid(P_PID, (id_t)pid, &info, WEXITED | WNOHANG | WNOWAIT);
    } while (r == -1 && errno == EINTR);
    if (r == -1) {
        return -1;
    }
    if (info.si_pid != pid) {
        return 0;
    }
    return (info.si_code == CLD_EXITED || info.si_code == CLD_KILLED || info.si_code == CLD_DUMPED) ? 1 : 0;
}

/*
 * Whether process group pgid has a living member other than exclude_pid: 1 if it has, 0 if not, -1
 * when membership cannot be determined. Zombies do not count. Used by a tree kill that holds the
 * group's leader as a zombie: that zombie keeps kill(-pgid, 0) succeeding, so it cannot tell when
 * everything else has gone.
 */
PTY_EXPORT int pty_group_has_live_members(int pgid, int exclude_pid)
{
#if defined(__APPLE__)
    int mib[4] = { CTL_KERN, KERN_PROC, KERN_PROC_PGRP, pgid };
    size_t len = 0;
    if (sysctl(mib, 4, NULL, &len, NULL, 0) == -1) {
        return -1;
    }
    /* Room for a few processes started between the two calls. */
    len += 16 * sizeof(struct kinfo_proc);
    struct kinfo_proc* procs = (struct kinfo_proc*)malloc(len);
    if (procs == NULL) {
        return -1;
    }
    /* A list that did not fit fails with ENOMEM rather than arriving cut short; a length that is not
     * whole records is not trusted either. Either way the answer is unknown, never empty. */
    if (sysctl(mib, 4, procs, &len, NULL, 0) == -1 || len % sizeof(struct kinfo_proc) != 0) {
        free(procs);
        return -1;
    }
    int n = (int)(len / sizeof(struct kinfo_proc));
    int live = 0;
    for (int i = 0; i < n; i++) {
        if (procs[i].kp_proc.p_pid != exclude_pid && procs[i].kp_proc.p_stat != SZOMB) {
            live = 1;
            break;
        }
    }
    free(procs);
    return live;
#else
    DIR* proc = opendir("/proc");
    if (proc == NULL) {
        return -1;
    }
    /*
     * Empty is only reported once every candidate has been read or confirmed gone. A candidate that
     * could not be read (EMFILE, EACCES, EIO, an unparseable line) or an enumeration that failed makes
     * the answer unknown, which the caller takes as "wait the whole grace period". Reading it as absent
     * would SIGKILL a live descendant still handling its SIGHUP.
     */
    int live = 0;
    int unknown = 0;
    struct dirent* entry;
    for (;;) {
        errno = 0;
        entry = readdir(proc);
        if (entry == NULL) {
            if (errno != 0) {
                unknown = 1;
            }
            break;
        }
        if (entry->d_name[0] < '0' || entry->d_name[0] > '9') {
            continue;
        }
        char dir[64];
        char path[80];
        snprintf(dir, sizeof(dir), "/proc/%s", entry->d_name);
        snprintf(path, sizeof(path), "%s/stat", dir);
        int fd;
        do {
            fd = open(path, O_RDONLY | O_CLOEXEC);
        } while (fd == -1 && errno == EINTR);
        if (fd == -1) {
            if (errno != ENOENT && errno != ESRCH) {
                unknown = 1;
            }
            continue; /* gone meanwhile, or recorded as unreadable */
        }
        char buf[512];
        ssize_t got;
        do {
            got = read(fd, buf, sizeof(buf) - 1);
        } while (got == -1 && errno == EINTR);
        int read_errno = errno;
        close(fd);
        if (got <= 0) {
            /* An exited process can leave an empty or ESRCH read behind; only a missing directory
             * confirms it is gone. */
            int gone = (got == 0 || read_errno == ESRCH) && access(dir, F_OK) == -1 && errno == ENOENT;
            if (!gone) {
                unknown = 1;
            }
            continue;
        }
        buf[got] = '\0';
        /* "pid (comm) state ppid pgrp ...": comm may itself hold ')', so take the last one. */
        char* end = strrchr(buf, ')');
        int pid = atoi(buf);
        char state;
        int ppid, pgrp;
        if (end == NULL || sscanf(end + 1, " %c %d %d", &state, &ppid, &pgrp) != 3) {
            unknown = 1;
            continue;
        }
        if (pgrp == pgid && pid != exclude_pid && state != 'Z' && state != 'X') {
            live = 1;
            break;
        }
    }
    closedir(proc);
    return live ? 1 : (unknown ? -1 : 0);
#endif
}

PTY_EXPORT int pty_get_errno(void)
{
    return errno;
}
