# Wire package

The Wire package is meant for internal use only. It provides to Unity services
the possibility to deliver real time messages to game clients using
Websocket.

Even though the Wire package is internal, it can still encounter issues 
preventing its proper functioning.

## Troubleshooting

### Assembly or namespace conflict with websocket-sharp

> [!WARNING]
> Support for `WIRE_EXCLUDE_WEBSOCKETSHARP` was removed in v1.2.2

Wire is relying on websocket-sharp to provide the Websocket implementation.
If you are using websocket-sharp in your project, you might encounter
conflicts with the version used by Wire.

To avoid this, you can use the following workaround:
* Go to Player Settings -> Other Settings -> Scripting Define Symbols
* Add the following symbol: `WIRE_EXCLUDE_WEBSOCKETSHARP`

The Wire websocket-sharp assembly won't be included in your build anymore.

> [!TIP]
> Please don't forget to add websocket-sharp to your project if you are using 
> it, do not rely on the one present in the Wire package, we might remove it in the
> future.
