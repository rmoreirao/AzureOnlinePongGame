// Entry point for the Pong frontend
import { initGame } from './game/game.js';
import { setupUI } from './components/ui.js';
import { connectSignalR } from './services/signalr.js';
import { setupAuth } from './services/auth.js';

window.addEventListener('DOMContentLoaded', () => {
    setupUI();
    setupAuth();
    initGame();
    connectSignalR();
});