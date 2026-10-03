# Arena Relay process

Run a Lobby first, then `dotnet run --project Samples/Arena/Relay/Arena.Relay.csproj -- --port 17892 --lobby-address 127.0.0.1 --lobby-port 17890 --bind 127.0.0.1 --ready-file relay-ready.json --report relay-stats.json`.
Startup fails nonzero when Lobby cannot be reached. The ready marker is written only after the relay listener starts. The report is atomically replaced approximately every 250 ms with raw `RelayStatistics` JSON. Neither report nor console output includes host credentials.

The default bind is loopback. To receive external Host and Client outbound connections, specify a deployment interface with `--bind`, publish its reachable DNS name/port via `ReserveRelayRoomAsync`, and configure firewall/NAT accordingly. `--tls-pfx /secure/relay.pfx --tls-password-env RELAY_PFX_PASSWORD` enables TLS on the relay; populate the named environment variable without passing its value on the command line. Clients must use a valid certificate chain and DNS-matching TLS target host; no accept-all certificate verification is supported.

Lobby supports `--bind IP --tls-pfx path --tls-password-env VAR` as optional settings. Relay may connect to that TLS Lobby with `--lobby-tls [--lobby-tls-target dns-name]`; without target override the lobby address is used for certificate hostname validation. Existing non-TLS Lobby connections remain supported. For public deployment, use TLS for both paths and provide network access controls; localhost tests do not establish Internet/NAT behavior.
