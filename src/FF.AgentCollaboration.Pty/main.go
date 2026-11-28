//go:build linux || darwin

// A local stdio bridge. The child gets a controlling tty; no network listener or shell is involved.
package main

import (
	"encoding/binary"
	"fmt"
	"io"
	"os"
	"os/exec"
	"os/signal"
	"strconv"
	"syscall"
	"time"

	"github.com/creack/pty"
)

func commands(master *os.File, source io.Reader) error {
	var header [5]byte
	for {
		if _, err := io.ReadFull(source, header[:]); err != nil {
			return err
		}
		size := binary.BigEndian.Uint32(header[1:])
		if size > 256*1024 {
			return fmt.Errorf("oversize PTY command")
		}
		data := make([]byte, size)
		if _, err := io.ReadFull(source, data); err != nil {
			return err
		}
		switch header[0] {
		case 1:
			if _, err := master.Write(data); err != nil {
				return err
			}
		case 2:
			if size != 4 {
				return fmt.Errorf("invalid PTY size")
			}
			if err := pty.Setsize(master, &pty.Winsize{Cols: binary.BigEndian.Uint16(data), Rows: binary.BigEndian.Uint16(data[2:])}); err != nil {
				return err
			}
		default:
			return fmt.Errorf("unknown PTY command")
		}
	}
}

func run(args []string) (int, error) {
	if len(args) < 3 {
		return 1, fmt.Errorf("expected columns, rows and executable")
	}
	cols, e1 := strconv.ParseUint(args[0], 10, 16)
	rows, e2 := strconv.ParseUint(args[1], 10, 16)
	if e1 != nil || e2 != nil || cols == 0 || rows == 0 {
		return 1, fmt.Errorf("invalid terminal dimensions")
	}
	child := exec.Command(args[2], args[3:]...)
	master, err := pty.StartWithSize(child, &pty.Winsize{Rows: uint16(rows), Cols: uint16(cols)})
	if err != nil {
		return 1, err
	}
	defer master.Close()
	pid := child.Process.Pid
	signal.Ignore(syscall.SIGPIPE)
	stops := make(chan os.Signal, 1)
	signal.Notify(stops, syscall.SIGHUP, syscall.SIGTERM, os.Interrupt)
	defer signal.Stop(stops)
	fmt.Fprintln(os.Stderr, "READY")
	incoming, outgoing, exited := make(chan error, 1), make(chan error, 1), make(chan error, 1)
	go func() { incoming <- commands(master, os.Stdin) }()
	go func() { _, err := io.Copy(os.Stdout, master); outgoing <- err }()
	go func() { exited <- child.Wait() }()
	var done, drained bool
	select {
	case <-exited:
		done = true
	case <-outgoing:
		drained = true
	case <-incoming:
	case <-stops:
	}
	// Close the native session on host EOF as well as on an explicit stop.
	syscall.Kill(-pid, syscall.SIGHUP)
	if !done {
		select {
		case <-exited:
		case <-time.After(1200 * time.Millisecond):
			syscall.Kill(-pid, syscall.SIGKILL)
			<-exited
		}
	}
	syscall.Kill(-pid, syscall.SIGKILL)
	if !drained {
		select {
		case <-outgoing:
		case <-time.After(time.Second):
		}
	}
	if code := child.ProcessState.ExitCode(); code >= 0 {
		return code, nil
	}
	return 128 + int(child.ProcessState.Sys().(syscall.WaitStatus).Signal()), nil
}

func main() {
	code, err := run(os.Args[1:])
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
	}
	os.Exit(code)
}
