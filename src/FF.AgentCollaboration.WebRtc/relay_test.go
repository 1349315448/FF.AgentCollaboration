package main

import (
	"fmt"
	"os"
	"path/filepath"
	"testing"

	"github.com/pion/webrtc/v4"
)

// go test -c builds the worker used to track and terminate isolated workers during reconnect tests.
func TestMain(m *testing.M) {
	if proof := os.Getenv("FF_TEST_RELAY_PROOF"); proof != "" {
		if err := os.WriteFile(filepath.Join(proof, fmt.Sprintf("%d", os.Getpid())), nil, 0600); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		policy := webrtc.ICETransportPolicyAll
		if os.Getenv("FF_TEST_RELAY_ONLY") == "1" {
			policy = webrtc.ICETransportPolicyRelay
		}
		if err := run(os.Stdin, os.Stdout, policy); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		return
	}
	os.Exit(m.Run())
}
