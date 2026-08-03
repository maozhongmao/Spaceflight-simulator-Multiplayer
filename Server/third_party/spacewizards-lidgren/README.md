# SpaceWizards.Lidgren.Network 0.3.1

- Upstream: https://github.com/space-wizards/SpaceWizards.Lidgren.Network
- NuGet: https://www.nuget.org/packages/SpaceWizards.Lidgren.Network/0.3.1
- Package repository commit: `1d85b82e058101b7ebd60cc8883af5359e4c263a`
- License: MIT (see `LICENSE`)
- Binary target: `net8.0`
- DLL SHA-256: `f85f8cc070412bb4509c1510da83f56f2e702f64dd431dd71a3b951120afd617`
- Downloaded NuGet SHA-256: `5697618a62bbb3d552153d6f7fc7e02a08a3a21884ebcac7230dbfe2bc214a3f`

This maintained fork includes the upstream fix for `NetReliableSenderChannel`
null dereference during ACK processing (commit `14364d2a9cdb20dc19e80e315d3a8bc7722648e3`,
released in 0.2.6). Version 0.3.1 is used by the .NET 8 server.

The original SFS 1.5-compatible Lidgren.Network 1.0.2 binary remains under
`third_party/lidgren/` only as a reference/client interoperability fixture. It is
not the server runtime dependency and is not distributed beside the single-file exe.
