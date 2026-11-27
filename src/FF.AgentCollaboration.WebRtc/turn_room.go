package main

import (
	"encoding/json"
	"errors"
	"io"
	"net"

	"github.com/pion/transport/v5/vnet"
	"github.com/pion/turn/v5"
)

// ICE needs UDP allocations even when the client reaches TURN over TCP.
// Reject RFC 6062 socket creation so a request cannot escape the virtual net.
type roomRelay struct {
	*turn.RelayAddressGeneratorStatic
}

func (roomRelay) AllocateListener(turn.AllocateListenerConfig) (net.Listener, net.Addr, error) {
	return nil, nil, errors.New("TCP allocations unsupported")
}

func (roomRelay) AllocateConn(turn.AllocateConnConfig) (net.Conn, error) {
	return nil, errors.New("TCP relays unsupported")
}

// Each room has a private virtual loopback network. TURN allocations can
// reach each other, but cannot send packets to the host, LAN or other rooms.
func serveTurnRoom(password string, input io.Reader, output *json.Encoder) error {
	if len(password) != 64 {
		return errors.New("invalid room credential")
	}
	roomNet, err := vnet.NewNet(&vnet.NetConfig{})
	if err != nil {
		return err
	}
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return err
	}
	defer listener.Close()
	const realm = "ff-acp-room"
	const user = "member"
	ip := net.IPv4(127, 0, 0, 1)
	key := turn.GenerateAuthKey(user, realm, password)
	server, err := turn.NewServer(turn.ServerConfig{
		Realm: realm,
		AuthHandler: func(request *turn.RequestAttributes) (string, []byte, bool) {
			return user, key, request.Username == user && request.Realm == realm
		},
		ListenerConfigs: []turn.ListenerConfig{{
			Listener: listener,
			RelayAddressGenerator: roomRelay{&turn.RelayAddressGeneratorStatic{
				RelayAddress: ip, Address: ip.String(), Net: roomNet,
			}},
			PermissionHandler: func(_ net.Addr, peer net.IP) bool { return ip.Equal(peer) },
		}},
	})
	if err != nil {
		return err
	}
	defer server.Close()
	if err := output.Encode(map[string]int{"port": listener.Addr().(*net.TCPAddr).Port}); err != nil {
		return err
	}
	_, err = io.Copy(io.Discard, input)
	return err
}
