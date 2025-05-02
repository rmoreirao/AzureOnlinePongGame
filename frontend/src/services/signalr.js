// Multiplayer SignalR connection logic
// Uses global signalR object from CDN

let connection = null;
let lastReconnectAttempt = 0;
let reconnectAttempts = 0;
let keepAliveInterval = null;

// Track the last sent paddle position
let lastSentPaddleY = null;

// Connection state enum for clarity
export const SignalRConnectionState = {
    Connected: 'Connected',
    Connecting: 'Connecting',
    Reconnecting: 'Reconnecting',
    Disconnected: 'Disconnected',
};

export async function connectSignalR(onGameUpdate, onMatchFound, onConnectionStateChange, onConnectionError, onWaitingForOpponent) {
    const backendUrl = "https://localhost:6001/pong"; // Updated to use HTTPS

    connection = new signalR.HubConnectionBuilder()
        .withUrl(backendUrl, {
            transport: signalR.HttpTransportType.WebSockets,
            skipNegotiation: true
        })
        .configureLogging(signalR.LogLevel.Information)
        .withAutomaticReconnect()
        .withHubProtocol(new signalR.protocols.msgpack.MessagePackHubProtocol())
        .build();

    window.signalRConnection = connection;

    // Add debug logging for incoming messages
    connection.on("GameUpdate", (gameState) => {
        console.debug("Received game update", gameState);
        onGameUpdate(gameState);
    });

    connection.on("MatchFound", (matchInfo) => {
        console.log("Match found event received", matchInfo);
        onMatchFound(matchInfo);
    });

    connection.on("WaitingForOpponent", () => {
        console.log("Waiting for opponent");
        if (onWaitingForOpponent) onWaitingForOpponent();
    });

    connection.on("AlreadyInGame", () => {
        console.log("Already in a game");
    });

    connection.on("OpponentDisconnected", (gameState) => {
        console.log("Opponent disconnected", gameState);
    });

    connection.on("GameOver", (gameState) => {
        console.log("Game over", gameState);
        onGameUpdate(gameState); // Update the final game state
    });

    connection.on("Pong", (timestamp) => {
        console.debug("Received pong from server", new Date(timestamp));
    });

    // Connection state handlers
    if (onConnectionStateChange) {
        connection.onclose((err) => {
            console.error("SignalR connection closed", err);
            if (onConnectionStateChange) onConnectionStateChange(SignalRConnectionState.Disconnected);
            if (onConnectionError && err) onConnectionError(err);
            stopKeepAlive();
        });

        connection.onreconnecting((err) => {
            console.warn("SignalR reconnecting due to error", err);
            if (onConnectionStateChange) onConnectionStateChange(SignalRConnectionState.Reconnecting);
        });

        connection.onreconnected((connectionId) => {
            console.log("SignalR reconnected with ID:", connectionId);
            reconnectAttempts = 0;
            if (onConnectionStateChange) onConnectionStateChange(SignalRConnectionState.Connected);
            startKeepAlive();
        });
    }

    try {
        await connection.start();
        console.log("Connected to SignalR");
        if (onConnectionStateChange) onConnectionStateChange(SignalRConnectionState.Connected);
        startKeepAlive();
    } catch (err) {
        console.error("SignalR connection error:", err);
        if (onConnectionStateChange) onConnectionStateChange(SignalRConnectionState.Disconnected);
        if (onConnectionError) onConnectionError(err);
    }
}

// Start sending regular keepalive messages
function startKeepAlive() {
    stopKeepAlive(); // Clear any existing interval
    
    // Send keepalive every 15 seconds
    keepAliveInterval = setInterval(() => {
        if (isConnected()) {
            console.debug("Sending keepalive");
            connection.invoke("KeepAlive").catch(err => {
                console.warn("Error sending keepalive:", err);
            });
        }
    }, 15000);
}

// Stop sending keepalive messages
function stopKeepAlive() {
    if (keepAliveInterval) {
        clearInterval(keepAliveInterval);
        keepAliveInterval = null;
    }
}

function isConnected() {
    return connection && connection.state === signalR.HubConnectionState.Connected;
}

export function sendPaddleUpdate(paddleY) {
    if (isConnected()) {
        try {
            // Only send updates if position changed or enough time has passed since last update
            const positionChanged = lastSentPaddleY === null || Math.abs(paddleY - lastSentPaddleY) > 0.01;
            const timeThresholdMet = !window.lastPaddleUpdate || Date.now() - window.lastPaddleUpdate > 50;
            
            if (positionChanged && timeThresholdMet) {
                connection.invoke("UpdatePaddle", paddleY).catch(err => {
                    console.warn("Error sending paddle update:", err);
                });
                window.lastPaddleUpdate = Date.now();
                lastSentPaddleY = paddleY;
            }
        } catch (err) {
            console.error("Error sending paddle update:", err);
        }
    }
}

export function joinMultiplayer() {
    if (isConnected()) {
        connection.invoke("JoinMatchmaking").catch(err => {
            console.error("Error joining matchmaking:", err);
        });
    }
}

export function startBotMatch() {
    if (isConnected()) {
        connection.invoke("StartBotMatch").catch(err => {
            console.error("Error starting bot match:", err);
        });
    }
}

// Helper function to check connection health
export function checkConnection() {
    if (!connection) {
        console.log("SignalR connection not initialized");
        return false;
    }
    
    console.log("SignalR connection state:", connection.state);
    return connection.state === signalR.HubConnectionState.Connected;
}

// Explicitly disconnect when the user leaves the page
window.addEventListener('beforeunload', () => {
    if (connection) {
        try {
            // Clean up resources
            stopKeepAlive();
            connection.stop();
        } catch (err) {
            console.warn("Error during connection cleanup:", err);
        }
    }
});