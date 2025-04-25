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
  - **A backend Bot in Multiplayer mode (Multiplayer Bot):**
    - User is paired with a server-side Bot that simulates paddle movement and participates in the real-time game loop.

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
- HTML5 + JavaScript using Canvas
- SignalR JavaScript client
- Azure Functions using C#
- GitHub + GitHub Actions for CI/CD