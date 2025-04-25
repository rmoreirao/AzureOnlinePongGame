# to run it locally

## Start the SignalR locally:

- Run: asrs-emulator upstream init
- Run: asrs-emulator start

## Redis cache
### to install it:
https://redis.io/docs/latest/operate/oss_and_stack/install/archive/install-redis/install-redis-on-windows/

### To run it (from Ubuntu WSL):
sudo service redis-server start


## To run the Functions

- Create file "local.settings.json" - use the "sample.local.settings.json" as example
- Update SignalR connection string from the emulator on api/local.settings.json
- Run from folder api/: func start --verbose 

