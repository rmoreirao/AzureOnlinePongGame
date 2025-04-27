import { enableMultiplayer, renderMultiplayerState, startLocalGame } from '../game/game.js';
import { connectSignalR, joinMultiplayer, sendPaddleUpdate, startBotMatch, SignalRConnectionState } from '../services/signalr.js';

let multiplayerActive = false;

// Toast notification utility
let toastContainer = null;
function showToast(message, duration = 3000, type = 'info') {
    if (!toastContainer) {
        toastContainer = document.createElement('div');
        toastContainer.id = 'toast-container';
        toastContainer.style.position = 'fixed';
        toastContainer.style.bottom = '32px';
        toastContainer.style.right = '32px';
        toastContainer.style.zIndex = '10001';
        toastContainer.style.display = 'flex';
        toastContainer.style.flexDirection = 'column';
        toastContainer.style.gap = '12px';
        document.body.appendChild(toastContainer);
    }
    const toast = document.createElement('div');
    toast.className = 'toast-message';
    toast.style.background = (type === 'error') ? '#b71c1c' : 'rgba(40,40,40,0.97)';
    toast.style.color = '#fff';
    toast.style.padding = '14px 28px';
    toast.style.borderRadius = '8px';
    toast.style.boxShadow = '0 2px 12px #0005';
    toast.style.fontSize = '1.08rem';
    toast.style.opacity = '1';
    toast.style.transition = 'opacity 0.5s';
    toast.innerText = message;
    toastContainer.appendChild(toast);
    setTimeout(() => {
        toast.style.opacity = '0';
        setTimeout(() => {
            if (toast.parentNode) toast.parentNode.removeChild(toast);
        }, 500);
    }, duration);
}

function showConnectionLostBanner(state) {
    // Only show a toast now, not a banner
    const msg = (state === SignalRConnectionState.Reconnecting)
        ? 'Connection lost. Reconnecting...'
        : 'Connection lost. Please refresh or try again later.';
    showToast(msg, 5000, 'error');
    // Disable controls
    document.getElementById('play-vs-player').disabled = true;
    document.getElementById('play-vs-bot').disabled = true;
}

function hideConnectionLostBanner() {
    // Only re-enable controls, no banner to hide
    document.getElementById('play-vs-player').disabled = false;
    document.getElementById('play-vs-bot').disabled = false;
}

function showErrorBanner(message) {
    // Only show error toast
    showToast(message, 5000, 'error');
}

// Patch: Listen for global unhandledrejection and error events
window.addEventListener('unhandledrejection', function(event) {
    if (event && event.reason && event.reason.message && event.reason.message.includes('Connection disconnected')) {
        showToast('Connection lost: ' + event.reason.message, 5000, 'error');
    } else if (event && event.reason && event.reason.message) {
        showToast('Error: ' + event.reason.message, 5000, 'error');
    }
});
window.addEventListener('error', function(event) {
    if (event && event.message && event.message.includes('Connection disconnected')) {
        showToast('Connection lost: ' + event.message, 5000, 'error');
    }
});

function onConnectionError(err) {
    let msg = 'Connection error';
    if (err && err.message) {
        msg += ': ' + err.message;
    } else if (typeof err === 'string') {
        msg += ': ' + err;
    }
    showErrorBanner(msg);
}

// Handles UI button events and visibility
export function setupUI() {
    document.getElementById('play-vs-local').onclick = () => {
        // Reset any active multiplayer state
        multiplayerActive = false;
        document.getElementById('play-vs-player').disabled = false;
        document.getElementById('play-vs-player').innerText = 'Play Online';
        document.getElementById('play-vs-bot').disabled = false;
        document.getElementById('play-vs-bot').innerText = 'Play vs Bot';
        
        // Start local game
        startLocalGame();
        showToast('Starting local game');
    };
    
    document.getElementById('play-vs-ai').onclick = () => showToast('Play vs AI not implemented');
    document.getElementById('play-vs-player').onclick = async () => {
        if (!multiplayerActive) {
            multiplayerActive = true;
            document.getElementById('play-vs-player').disabled = true;
            document.getElementById('play-vs-player').innerText = 'Connecting...';
            await connectSignalR(onGameUpdate, onMatchFound, onConnectionStateChange, onConnectionError, onWaitingForOpponent);
            joinMultiplayer();
        }
    };
    document.getElementById('play-vs-bot').onclick = async () => {
        if (!multiplayerActive) {
            multiplayerActive = true;
            document.getElementById('play-vs-bot').disabled = true;
            document.getElementById('play-vs-bot').innerText = 'Connecting...';
            await connectSignalR(onGameUpdate, onMatchFound, onConnectionStateChange, onConnectionError, onWaitingForOpponent);
            startBotMatch();
        }
    };
    document.getElementById('leaderboard').onclick = () => showToast('Leaderboard not implemented');
    document.getElementById('login').onclick = () => showToast('Login not implemented');
    document.getElementById('logout').onclick = () => showToast('Logout not implemented');
}

function onConnectionStateChange(state) {
    if (state === SignalRConnectionState.Disconnected || state === SignalRConnectionState.Reconnecting) {
        showConnectionLostBanner(state);
    } else if (state === SignalRConnectionState.Connected) {
        hideConnectionLostBanner();
    }
}

function onGameUpdate(gameState) {
    renderMultiplayerState(gameState);
}

function onWaitingForOpponent() {
    showToast('Waiting for an opponent...', 5000);
    document.getElementById('play-vs-player').innerText = 'Waiting...';
}

function onMatchFound(matchInfo) {
    // Enable multiplayer mode in game logic
    enableMultiplayer(matchInfo.side, sendPaddleUpdate);
    showToast('Match found! Starting game...');
    document.getElementById('play-vs-player').innerText = 'Play Online';
    document.getElementById('play-vs-player').disabled = false;
    document.getElementById('play-vs-bot').innerText = 'Play vs Bot';
    document.getElementById('play-vs-bot').disabled = false;
    multiplayerActive = false;
}