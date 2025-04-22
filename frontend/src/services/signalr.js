// Multiplayer SignalR connection logic
// Uses global signalR object from CDN

let connection = null;

export async function connectSignalR(onGameUpdate, onMatchFound) {
    // Call your negotiate endpoint (local Azure Functions)
    const res = await fetch('http://localhost:7071/api/negotiate', { method: 'POST' });
    const info = await res.json();

    connection = new signalR.HubConnectionBuilder()
        .withUrl(info.url, { accessTokenFactory: () => info.accessToken })
        .withAutomaticReconnect()
        .build();

    connection.on("GameUpdate", onGameUpdate);
    connection.on("MatchFound", onMatchFound);

    try {
        await connection.start();
        console.log("Connected to SignalR");
    } catch (err) {
        console.error("SignalR connection error:", err);
    }
}

export function sendPaddleUpdate(paddleY) {
    if (connection) {
        connection.invoke("UpdatePaddle", { y: paddleY });
    }
}

export function joinMultiplayer() {
    if (connection) {
        connection.invoke("JoinMatchmaking");
    }
}