# Timy USB SDK

The local SDK binaries were extracted, without modification, from:
`F:\timy3\software\AlgeTimyUsbDLLExample.zip`.

Run `scripts/import-timy-sdk.ps1` if the binaries are missing. The vendor files are excluded from Git.
`AlgeTimyUsb.Dummy.dll` is used **only** as a compile-time reference with `Private=false`.
It must never be deployed beside an executable. Production uses `AlgeTimyUsb.x64.dll`
resolved by the bridge at runtime, plus its bundled VC runtime dependencies.

The bridge never calls Send, clears device memory, changes timing parameters or updates firmware.
Opening a connection uses the vendor SDK's standard USB interface configuration operation.
