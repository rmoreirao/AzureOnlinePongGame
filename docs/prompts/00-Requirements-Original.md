# The Project

Azure-hosted multiplayer Pong game

# Requirements

## ✅ Functional Requirements

### Core Gameplay
- Players can control paddles with keyboard/mouse input.
- A ball bounces between paddles and the top/bottom walls.
- Scoring system with game restart on win/loss.
- Play against:
  - A computer (simple AI paddle logic)
  - Another real player online (real-time)

### Multiplayer Features
- Real-time paddle and ball movement updates
- Matchmaking: auto-pair two players in a room
- Game loop hosted server-side (authoritative logic)
- Game restart functionality
- Chat (optional)

### User Features
- Join as guest or login with identity (e.g., GitHub/Microsoft via Azure Static Web Apps auth)
- View leaderboards (top scores by player)
- Optional rematch or return to lobby

---

## 🛠 Technical Requirements

### Azure Services
| Component | Service |
|----------|---------|
| Frontend hosting | Azure Static Web Apps |
| Real-time messaging | Azure SignalR Service |
| Server-side logic | Azure Functions |
| Transient game state | Azure Cache for Redis |
| Persistent data | Azure Cosmos DB |
| Authentication | Static Web Apps Auth (with social identity providers) |
| Monitoring | Azure Application Insights / Monitor |

### Stack
- HTML5 + JavaScript (Phaser.js or Canvas)
- SignalR JavaScript client
- Azure Functions (Node.js or Python)
- GitHub + GitHub Actions for CI/CD
- Optional: PlayFab or App Configuration if expanding

---

## 🪜 Step-by-Step Implementation Plan

### Phase 1: Setup Infrastructure

1. Create Azure Static Web App
   - Connect to GitHub repo with game front-end
   - Enable built-in auth with social login
2. Create Azure SignalR Service (Standard tier for scale-out groups)
3. Set up Azure Functions App
   - Add negotiate(), matchmaker(), and gameLoop() endpoints
4. Create Azure Cosmos DB (for leaderboard, player profiles)
5. Create Azure Cache for Redis (for active game states)
6. Add monitoring with Application Insights

---

### Phase 2: Build Game Front-End (Client)

1. Implement Pong game using Phaser.js or Canvas
2. Handle keyboard inputs and paddle rendering
3. Add SignalR client:
   - Connect via negotiate() function
   - Send paddle input events
   - Listen to broadcasted game state (ball, paddles, score)
4. Render UI overlays:
   - Lobby / waiting screen
   - Match found
   - Win/lose state
5. Implement matchmaking event handling
6. (Optional) Add chat panel using SignalR groups

---

### Phase 3: Server-Side Functions (Node.js or Python)

1. negotiate(): Issues SignalR connection info
2. matchmaker(): Queues incoming players, pairs them in SignalR group
3. gameLoop():
   - Timer-triggered (~60 times/second)
   - Reads paddle states from Redis
   - Updates ball physics, checks collisions, updates score
   - Pushes state to SignalR group
4. paddleMoveHandler(): Updates paddle direction in Redis

---

### Phase 4: Leaderboard & Profiles

1. When match ends:
   - Log game result in Cosmos DB (timestamp, player names, scores)
2. Create leaderboard API (Azure Function):
   - GET /api/leaderboard → top N scores
3. Display leaderboard in front-end
4. (Optional) Allow users to pick display names or use their auth profile

---

### Phase 5: CI/CD and Monitoring

1. Set up GitHub Actions to deploy Static Web App and Functions
2. Add Application Insights to:
   - Monitor latency
   - Track SignalR connections/disconnects
   - Trace error logs from Functions

---

## ➕ Optional Flows (Extras)

### Rematch / Return to Lobby
- After game ends, client sends rematch request
- Function re-queues players for matchmaking

### Spectator Mode
- Clients can join SignalR group as read-only and view gameplay in real-time

### Chat Flow
- SignalR group per match supports messages
- Function relays chat events

---

Would you like this turned into a project plan or code templates to get started quickly (e.g. Static Web App scaffold, SignalR negotiate Function, etc.)?