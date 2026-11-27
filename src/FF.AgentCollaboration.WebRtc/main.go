package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/pion/webrtc/v4"
)

// 信令由工作台完成。子进程只通过标准输入输出交换 SDP，
// 并通过 DataChannel 建立到提供方环回联邦监听的字节流。
type startRequest struct {
	Role           string
	FederationPort int
	Offer          string
	IceServers     []webrtc.ICEServer
	Password       string
}

const (
	dataLabel       = "ff-acp-federation"
	maxLocalStreams = 8
	blockSize       = 16 * 1024
)

func main() {
	if err := run(os.Stdin, os.Stdout, webrtc.ICETransportPolicyAll); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func run(input io.Reader, output io.Writer, policy webrtc.ICETransportPolicy) error {
	decoder := json.NewDecoder(input)
	writer := json.NewEncoder(output)
	var request startRequest
	if err := decoder.Decode(&request); err != nil {
		return err
	}
	if request.Role == "turn-server" {
		return serveTurnRoom(request.Password, input, writer)
	}
	provider := request.Role == "provider"
	if (provider && (request.FederationPort < 1 || request.FederationPort > 65535)) ||
		(!provider && (request.Role != "requester" || request.Offer == "")) {
		return errors.New("invalid RTC bridge launch")
	}
	var settings webrtc.SettingEngine
	settings.DetachDataChannels()
	connection, err := webrtc.NewAPI(webrtc.WithSettingEngine(settings)).NewPeerConnection(webrtc.Configuration{
		ICEServers: request.IceServers, ICETransportPolicy: policy,
	})
	if err != nil {
		return err
	}
	defer connection.Close()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	ready := make(chan struct{}, 1)
	connection.OnConnectionStateChange(func(state webrtc.PeerConnectionState) {
		switch state {
		case webrtc.PeerConnectionStateConnected:
			select {
			case ready <- struct{}{}:
			default:
			}
		case webrtc.PeerConnectionStateFailed, webrtc.PeerConnectionStateClosed:
			cancel()
		}
	})

	if provider {
		serveProvider(ctx, connection, request.FederationPort)
		bootstrap, err := connection.CreateDataChannel("negotiate", nil)
		if err != nil {
			return err
		}
		bootstrap.OnOpen(func() {
			wire, err := bootstrap.Detach()
			if err == nil {
				_ = wire.Close()
			}
		})
		offer, err := connection.CreateOffer(nil)
		if err != nil {
			return err
		}
		if err := publishDescription(ctx, connection, offer, writer); err != nil {
			return err
		}
		var response struct{ Answer string }
		if err := decoder.Decode(&response); err != nil {
			return err
		}
		if err := connection.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeAnswer, SDP: response.Answer}); err != nil {
			return err
		}
	} else {
		// 实际字节流由请求方发起。初始通道只用于建立 SCTP。
		connection.OnDataChannel(func(channel *webrtc.DataChannel) {
			channel.OnOpen(func() {
				wire, err := channel.Detach()
				if err == nil {
					_ = wire.Close()
				}
			})
		})
		if err := connection.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer, SDP: request.Offer}); err != nil {
			return err
		}
		answer, err := connection.CreateAnswer(nil)
		if err != nil {
			return err
		}
		if err := publishDescription(ctx, connection, answer, writer); err != nil {
			return err
		}
	}
	go func() {
		_, _ = io.Copy(io.Discard, input)
		cancel()
	}()
	select {
	case <-ready:
	case <-ctx.Done():
		return ctx.Err()
	case <-time.After(30 * time.Second):
		return errors.New("WebRTC connection timed out")
	}
	if provider {
		if err := writer.Encode(map[string]bool{"ready": true}); err != nil {
			return err
		}
		<-ctx.Done()
		return nil
	}
	return serveRequester(ctx, connection, writer)
}

func publishDescription(ctx context.Context, connection *webrtc.PeerConnection, description webrtc.SessionDescription, writer *json.Encoder) error {
	gathered := webrtc.GatheringCompletePromise(connection)
	if err := connection.SetLocalDescription(description); err != nil {
		return err
	}
	select {
	case <-gathered:
	case <-ctx.Done():
		return ctx.Err()
	case <-time.After(12 * time.Second):
		// A slow optional STUN/TURN service must not discard already gathered paths.
		if !strings.Contains(connection.LocalDescription().SDP, "a=candidate:") {
			return errors.New("ICE gathering timed out without candidates")
		}
	}
	return writer.Encode(map[string]string{"sdp": connection.LocalDescription().SDP})
}

func serveProvider(ctx context.Context, connection *webrtc.PeerConnection, port int) {
	active := make(chan struct{}, maxLocalStreams)
	connection.OnDataChannel(func(channel *webrtc.DataChannel) {
		if channel.Label() != dataLabel {
			_ = channel.Close()
			return
		}
		channel.OnOpen(func() {
			select {
			case active <- struct{}{}:
				defer func() { <-active }()
			default:
				_ = channel.Close()
				return
			}
			wire, err := channel.Detach()
			if err != nil {
				_ = channel.Close()
				return
			}
			defer wire.Close()
			dialer := net.Dialer{Timeout: 5 * time.Second}
			source, err := dialer.DialContext(ctx, "tcp4", net.JoinHostPort("127.0.0.1", strconv.Itoa(port)))
			if err != nil {
				return
			}
			forward(source, wire)
		})
	})
}

func serveRequester(ctx context.Context, connection *webrtc.PeerConnection, writer *json.Encoder) error {
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return err
	}
	defer listener.Close()
	if err := writer.Encode(map[string]int{"port": listener.Addr().(*net.TCPAddr).Port}); err != nil {
		return err
	}
	stop := context.AfterFunc(ctx, func() { _ = listener.Close() })
	defer stop()
	active := make(chan struct{}, maxLocalStreams)
	for {
		socket, err := listener.Accept()
		if ctx.Err() != nil {
			return nil
		}
		if err != nil {
			return err
		}
		select {
		case active <- struct{}{}:
			go func() {
				defer func() { <-active }()
				defer socket.Close()
				openStream(ctx, connection, socket)
			}()
		default:
			_ = socket.Close()
		}
	}
}

func openStream(ctx context.Context, connection *webrtc.PeerConnection, socket net.Conn) {
	channel, err := connection.CreateDataChannel(dataLabel, nil)
	if err != nil {
		return
	}
	defer channel.Close()
	ready := make(chan struct{})
	channel.OnOpen(func() { close(ready) })
	select {
	case <-ready:
	case <-ctx.Done():
		return
	case <-time.After(8 * time.Second):
		return
	}
	wire, err := channel.Detach()
	if err == nil {
		forward(socket, wire)
	}
}

func forward(socket net.Conn, wire io.ReadWriteCloser) {
	finished := make(chan struct{})
	go func() {
		_, _ = io.CopyBuffer(wire, socket, make([]byte, blockSize))
		_ = wire.Close()
		close(finished)
	}()
	_, _ = io.CopyBuffer(socket, wire, make([]byte, blockSize))
	_ = socket.Close()
	_ = wire.Close()
	<-finished
}
