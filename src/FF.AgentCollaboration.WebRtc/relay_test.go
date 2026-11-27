package main

import (
	"fmt"
	"os"
	"path/filepath"
	"testing"

	"github.com/pion/webrtc/v4"
)

// go test -c builds the worker used by the two-workbench relay regression.
// The test binary offers no host or server-reflexive candidates in this mode.
func TestMain(m *testing.M) {
	if os.Getenv("FF_TEST_RELAY_ONLY") == "1" {
		if err := os.WriteFile(filepath.Join(os.Getenv("FF_TEST_RELAY_PROOF"), fmt.Sprintf("%d", os.Getpid())), nil, 0600); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		if err := run(os.Stdin, os.Stdout, webrtc.ICETransportPolicyRelay); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		return
	}
	os.Exit(m.Run())
}
