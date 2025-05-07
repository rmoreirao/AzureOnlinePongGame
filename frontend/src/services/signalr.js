// Multiplayer SignalR connection logic
// Uses global signalR object from CDN

import { enableMultiplayer, renderMultiplayerState, handleOpponentInput } from '../game/game.js';
import { SIGNALR_HUB_URL } from '../config.js';

let connection = null;
let lastReconnectAttempt = 0;
let reconnectAttempts = 0;
let keepAliveInterval = null;

// Connection state enum for clarity
export const SignalRConnectionState = {
    Connected: 'Connected',
    Connecting: 'Connecting',
    Reconnecting: 'Reconnecting',
    Disconnected: 'Disconnected',
};

export async function connectSignalR(onGameUpdate, onMatchFound, onConnectionStateChange, onConnectionError, onWaitingForOpponent) {
    connection = new signalR.HubConnectionBuilder()
        .withUrl(SIGNALR_HUB_URL)
        .configureLogging(signalR.LogLevel.Information)
        .withAutomaticReconnect()
        .withHubProtocol(new signalR.protocols.msgpack.MessagePackHubProtocol())
        .build();

    window.signalRConnection = connection;

    // Game state update from server
    connection.on("GameUpdate", (gameState) => {
        renderMultiplayerState(gameState);
        if (onGameUpdate) onGameUpdate(gameState);
    });

    // Opponent input from server
    connection.on("OpponentPaddleInput", (targetY) => {
        handleOpponentInput(targetY);
    });

    connection.on("MatchFound", (matchInfo) => {
        // matchInfo.side: 1 or 2
        enableMultiplayer(matchInfo.side, sendPaddleInput);
        // Signal readiness to backend
        if (connection && typeof connection.invoke === 'function') {
            connection.invoke("RequestStartGame");
        }
        if (onMatchFound) onMatchFound(matchInfo);
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
        if (onGameUpdate) onGameUpdate(gameState); // Update the final game state
    });

    connection.on("Pong", (timestamp) => {
        console.debug("Received pong from server", new Date(timestamp));
    });

    connection.on("GameStarted", () => {
        // Optionally, you can show a toast or UI indicator that the game is starting
        console.log("Game started! Both players are ready.");
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

// Use new SendPaddleInput method
export function sendPaddleInput(targetY) {
    if (isConnected()) {
        try {
            connection.invoke("SendPaddleInput", targetY).catch(err => {
                console.warn("Error sending paddle input:", err);
            });
        } catch (err) {
            console.error("Error sending paddle input:", err);
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