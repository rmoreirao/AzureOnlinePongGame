// Multiplayer SignalR connection logic
// Uses global signalR object from CDN

let connection = null;

// Connection state enum for clarity
export const SignalRConnectionState = {
    Connected: 'Connected',
    Connecting: 'Connecting',
    Reconnecting: 'Reconnecting',
    Disconnected: 'Disconnected',
};

export async function connectSignalR(onGameUpdate, onMatchFound, onConnectionStateChange, onConnectionError) {
    // Call your negotiate endpoint (local Azure Functions)
    const res = await fetch('http://localhost:7071/api/negotiate', { method: 'POST' });
    const info = await res.json();

    connection = new signalR.HubConnectionBuilder()
        .withUrl(info.url, { accessTokenFactory: () => info.accessToken })
        .withAutomaticReconnect()
        .build();

    // Expose connection globally for bot mode
    window.signalRConnection = connection;

    connection.on("GameUpdate", onGameUpdate);
    connection.on("MatchFound", onMatchFound);

    // Connection state handlers
    if (onConnectionStateChange) {
        connection.onclose((err) => {
            if (onConnectionStateChange) onConnectionStateChange(SignalRConnectionState.Disconnected);
            if (onConnectionError && err) onConnectionError(err);
        });
        connection.onreconnecting(() => onConnectionStateChange(SignalRConnectionState.Reconnecting));
        connection.onreconnected(() => onConnectionStateChange(SignalRConnectionState.Connected));
    }

    try {
        await connection.start();
        console.log("Connected to SignalR");
        if (onConnectionStateChange) onConnectionStateChange(SignalRConnectionState.Connected);
    } catch (err) {
        console.error("SignalR connection error:", err);
        if (onConnectionStateChange) onConnectionStateChange(SignalRConnectionState.Disconnected);
    }
}

function isConnected() {
    return connection && connection.state === signalR.HubConnectionState.Connected;
}

export function sendPaddleUpdate(paddleY) {
    if (isConnected()) {
        connection.invoke("UpdatePaddle", { y: paddleY });
    }
}

export function joinMultiplayer() {
    if (isConnected()) {
        connection.invoke("JoinMatchmaking");
    }
}

export function startBotMatch() {
    if (isConnected()) {
        connection.invoke("StartBotMatch");
    }
}