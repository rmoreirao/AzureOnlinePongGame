// Multiplayer SignalR connection logic
import * as signalR from "@microsoft/signalr";

let connection = null;

export async function connectSignalR(onGameUpdate, onMatchFound) {
    // TODO: Replace with your Azure SignalR endpoint
    const SIGNALR_URL = "<YOUR_AZURE_SIGNALR_FUNCTION_ENDPOINT>";
    connection = new signalR.HubConnectionBuilder()
        .withUrl(SIGNALR_URL)
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