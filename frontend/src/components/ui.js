import { enableMultiplayer, renderMultiplayerState } from '../game/game.js';
import { connectSignalR, joinMultiplayer, sendPaddleUpdate } from '../services/signalr.js';

let multiplayerActive = false;

// Handles UI button events and visibility
export function setupUI() {
    document.getElementById('play-vs-ai').onclick = () => alert('Play vs AI not implemented');
    document.getElementById('play-vs-player').onclick = async () => {
        if (!multiplayerActive) {
            multiplayerActive = true;
            document.getElementById('play-vs-player').disabled = true;
            document.getElementById('play-vs-player').innerText = 'Searching...';
            await connectSignalR(onGameUpdate, onMatchFound);
            joinMultiplayer();
        }
    };
    document.getElementById('play-vs-bot').onclick = async () => {
        if (!multiplayerActive) {
            multiplayerActive = true;
            document.getElementById('play-vs-bot').disabled = true;
            document.getElementById('play-vs-bot').innerText = 'Searching...';
            await connectSignalR(onGameUpdate, onMatchFound);
            startBotMatch();
        }
    };
    document.getElementById('leaderboard').onclick = () => alert('Leaderboard not implemented');
    document.getElementById('login').onclick = () => alert('Login not implemented');
    document.getElementById('logout').onclick = () => alert('Logout not implemented');
}

function onGameUpdate(gameState) {
    renderMultiplayerState(gameState);
}

function onMatchFound(matchInfo) {
    // Enable multiplayer mode in game logic
    enableMultiplayer(matchInfo.side, sendPaddleUpdate);
    alert('Match found! Starting game...');
    document.getElementById('play-vs-player').innerText = 'Play Online';
    document.getElementById('play-vs-player').disabled = false;
    document.getElementById('play-vs-bot').innerText = 'Play vs Bot';
    document.getElementById('play-vs-bot').disabled = false;
    multiplayerActive = false;
}

function startBotMatch() {
    if (window.signalRConnection && window.signalRConnection.invoke) {
        window.signalRConnection.invoke("StartBotMatch");
    } else if (typeof sendStartBotMatch === 'function') {
        sendStartBotMatch();
    } else if (typeof window.connection !== 'undefined' && window.connection.invoke) {
        window.connection.invoke("StartBotMatch");
    } else {
        // fallback: use the exported function from signalr.js
        if (typeof window.startBotMatch === 'function') window.startBotMatch();
    }
}