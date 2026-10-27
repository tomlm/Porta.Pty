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

/* glibc declares pipe2, used for the exec report pipe, only under _GNU_SOURCE. */
#if !defined(__APPLE__) && !defined(_GNU_SOURCE)
    #define _GNU_SOURCE
#endif

#if defined(__APPLE__)
    #include <util.h>
    #include <sys/ioctl.h>
    #include <sys/event.h>
    #include <sys/time.h>
    #include <libproc.h>
#else
    #include <pty.h>
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
 * Reports a failed chdir or exec of the child to the parent, then ends the child. Only write(2) and
 * _exit(2), both async-signal-safe.
 */
static void pty_child_failed(int report_fd, int err)
{
    ssize_t written;
    do {
        written = write(report_fd, &err, sizeof(err));
    } while (written == -1 && errno == EINTR);
    _exit(err);
}

/*
 * Creates the exec report pipe with both ends close-on-exec at creation. pipe2 exists on Linux and,
 * on macOS, from macOS 27; a shim built for an older macOS sets the flag right after pipe(2), and a
 * process that another thread forks in that instant keeps the write end and delays this spawn until
 * it exits.
 */
static int pty_report_pipe(int fds[2])
{
#if defined(__APPLE__) && __ENVIRONMENT_MAC_OS_X_VERSION_MIN_REQUIRED__ < 270000
    if (pipe(fds) != 0) {
        return -1;
    }
    fcntl(fds[0], F_SETFD, FD_CLOEXEC);
    fcntl(fds[1], F_SETFD, FD_CLOEXEC);
    return 0;
#else
    return pipe2(fds, O_CLOEXEC);
#endif
}

/*
 * Waits for the child to exec or to report why it could not. Returns 0 once exec has closed the
 * close-on-exec write end (end of file, nothing read), or the child's errno. In that case the child
 * is collected here, since nobody else will ever learn its pid.
 */
static int pty_wait_for_exec(int report_fd, pid_t pid)
{
    int child_errno = 0;
    size_t got = 0;
    while (got < sizeof(child_errno)) {
        ssize_t n = read(report_fd, (char*)&child_errno + got, sizeof(child_errno) - got);
        if (n == -1 && errno == EINTR) {
            continue;
        }
        if (n <= 0) {
            break;
        }
        got += (size_t)n;
    }
    if (got < sizeof(child_errno)) {
        return 0;
    }

    int status;
    while (waitpid(pid, &status, 0) == -1 && errno == EINTR) {
    }
    return child_errno > 0 ? child_errno : EIO;
}

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
 * since this runs between fork and exec of a multithreaded host. keep stays open: it is the write end
 * of the pipe that reports a failed chdir or exec to the parent, and it is close-on-exec already.
 *
 * macOS marks instead of closing, as Crashpad does: close() of a descriptor with a close guard
 * (guarded_open_np, change_fdguard_np) raises EXC_GUARD and kills the child, while setting
 * close-on-exec is allowed, and exec then closes the descriptors itself.
 */
static void pty_close_inherited_fds(int keep)
{
#if defined(__APPLE__)
    struct proc_fdinfo fds[512];
    int bytes = proc_pidinfo(getpid(), PROC_PIDLISTFDS, 0, fds, (int)sizeof(fds));
    if (bytes > 0) {
        int n = bytes / (int)sizeof(fds[0]);
        for (int i = 0; i < n; i++) {
            if (fds[i].proc_fd > 2 && fds[i].proc_fd != keep) {
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
    if ((keep <= 3 || syscall(SYS_close_range, 3U, (unsigned int)keep - 1, 0U) == 0)
        && syscall(SYS_close_range, (unsigned int)(keep < 3 ? 3 : keep + 1), ~0U, 0U) == 0) {
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
                    if (fd > 2 && fd != dir && fd != keep) {
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
        if (fd != keep) {
#if defined(__APPLE__)
            fcntl((int)fd, F_SETFD, FD_CLOEXEC);
#else
            close((int)fd);
#endif
        }
    }
}

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

    /*
     * The pipe that tells a failed chdir or exec from a program that ran and exited. Both ends are
     * close-on-exec from the moment they exist, so a successful exec closes the write end and the
     * parent reads end of file; a failure writes the child's errno first. Without it, a missing
     * program or working folder looked like a program that ran and exited with 2 (ENOENT, passed
     * to _exit). Any other process that inherited the write end would hold it open across its own
     * exec and keep the parent's read waiting until that process EXITS, so the flag is set by
     * pipe2 itself, never after.
     *
     * Both ends then move above the two numbers pipe2 took, which go back to forkpty, so the
     * controller still gets the lowest free number, as it did before this pipe existed;
     * F_DUPFD_CLOEXEC keeps the flag on the copies.
     */
    int report[2];
    int pipe_err = pty_report_pipe(report) == 0 ? 0 : errno;
    if (pipe_err == 0) {
        int floor = (report[0] > report[1] ? report[0] : report[1]) + 1;
        for (int end = 0; end < 2; end++) {
            int moved = fcntl(report[end], F_DUPFD_CLOEXEC, floor);
            if (moved == -1 && pipe_err == 0) {
                pipe_err = errno;
            }
            close(report[end]);
            report[end] = moved;
        }
        if (pipe_err != 0) {
            if (report[0] != -1) close(report[0]);
            if (report[1] != -1) close(report[1]);
        }
    }
    if (pipe_err != 0) {
        result.error = pipe_err;
        pthread_mutex_unlock(&pty_spawn_lock);
        pthread_sigmask(SIG_SETMASK, &saved_mask, NULL);
        return result;
    }

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
        close(report[1]);
    }
    
    if (pid == -1) {
        /* forkpty failed */
        close(report[0]);
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

        close(report[0]);
        pty_close_inherited_fds(report[1]);

        /* Change working directory if specified */
        if (working_dir != NULL && working_dir[0] != '\0') {
            if (chdir(working_dir) == -1) {
                pty_child_failed(report[1], errno);
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
        pty_child_failed(report[1], errno);
    }
    
    /* Parent process: the spawn has succeeded only once the program has replaced the child. */
    int exec_errno = pty_wait_for_exec(report[0], pid);
    close(report[0]);
    if (exec_errno != 0) {
        close(master_fd);
        result.error = exec_errno;
        return result;
    }

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

PTY_EXPORT int pty_get_errno(void)
{
    return errno;
}
