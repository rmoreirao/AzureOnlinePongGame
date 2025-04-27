# Azure Online Pong Game

A real-time multiplayer Pong game built with Azure Functions, SignalR, and Redis. Play against AI, friends, or server-side bots in this classic arcade game reimagined with modern cloud architecture.

## 🎮 Features

- Real-time multiplayer gameplay via Azure SignalR
- Single-player mode against AI
- Server-side bot matches for reliable gameplay
- Session persistence with Redis Cache
- Matchmaking system for online play
- Leaderboard for tracking top players

## 📋 Prerequisites

- [Azure Functions Core Tools](https://docs.microsoft.com/en-us/azure/azure-functions/functions-run-local)
- [SignalR Service Emulator](https://github.com/Azure/azure-signalr/blob/dev/docs/emulator.md) for local development
- [Redis](https://redis.io/docs/latest/operate/oss_and_stack/install/archive/install-redis/install-redis-on-windows/) for session storage
- Node.js for frontend development

## 🚀 Getting Started

### SignalR Service Setup
To run the SignalR service locally with the emulator:

```bash
# Initialize the upstream connections
asrs-emulator upstream init

# Start the emulator
asrs-emulator start
```

### Redis Cache Setup

#### Installation:
Follow the Redis installation guide for your platform:
- Windows: https://redis.io/docs/latest/operate/oss_and_stack/install/archive/install-redis/install-redis-on-windows/

#### Running Redis (from Ubuntu WSL):
```bash
sudo service redis-server start
```

### Azure Functions Setup

1. Create your configuration file:
   ```
   cp api/sample.local.settings.json api/local.settings.json
   ```

2. Update the SignalR connection string in `api/local.settings.json` with the one from the emulator

3. Start the Functions host:
   ```bash
   cd api
   func start --verbose
   ```

### Running the Frontend

1. Serve the frontend files using a local server
2. Navigate to the local server address in your browser

## 🎲 Gameplay Modes

- **Single Player**: Play against a simple AI opponent
- **Play vs Bot**: Play against a server-side bot with more consistent gameplay
- **Play Online**: Get matched with another player for real-time multiplayer

## 🏗️ System Architecture

The game is built using a serverless architecture:

- **Frontend**: Static HTML/CSS/JS with Canvas for rendering
- **Backend**: Azure Functions with SignalR triggers
- **Real-time Communication**: Azure SignalR Service
- **State Management**: Redis Cache for game sessions, matchmaking, and leaderboard

For detailed architecture information, see [SYSTEM_DESIGN.md](SYSTEM_DESIGN.md)

## 📦 Deployment

For production deployment:
- Host the frontend on Azure Static Web Apps
- Deploy functions to Azure Functions
- Provision Azure SignalR Service
- Set up Azure Cache for Redis

## 📄 License

See [LICENSE](LICENSE) file for details.

