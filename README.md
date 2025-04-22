# to run it locally

## Start the SignalR locally:

- Run: asrs-emulator upstream init
- Run: asrs-emulator start

## To run the Functions

- Create file "local.settings.json" - use the "sample.local.settings.json" as example
- Update SignalR connection string from the emulator on api/local.settings.json
- Run from folder api/: func start --verbose 