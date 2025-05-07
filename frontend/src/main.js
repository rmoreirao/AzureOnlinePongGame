// Main entry point for the application
import { initGame, startLocalGame, enableMultiplayer, renderMultiplayerState, handleOpponentInput, updatePing } from './game/game.js';

document.addEventListener('DOMContentLoaded', () => {
    // Initialize game canvas and controls
    initGame();
    
    // UI elements
    const playVsLocalBtn = document.getElementById('play-vs-local');
    const playVsAiBtn = document.getElementById('play-vs-ai');
    const playVsPlayerBtn = document.getElementById('play-vs-player');
    const playVsBotBtn = document.getElementById('play-vs-bot');
    const connectionStatus = document.getElementById('connection-status');
    const toggleDebugCheckbox = document.getElementById('toggle-debug');
    
    // Show connection status
    connectionStatus.textContent = 'Disconnected';
    connectionStatus.style.backgroundColor = '#d32f2f';
    connectionStatus.style.display = 'block';
    
    // Setup debug toggle
    toggleDebugCheckbox.addEventListener('change', function() {
        const debugPanel = document.getElementById('debug-panel');
        const debugCanvas = document.getElementById('debug-canvas');
        
        if (this.checked) {
            debugPanel.style.display = 'block';
            debugCanvas.style.display = 'block';
            console.log('Debug mode enabled');
        } else {
            debugPanel.style.display = 'none';
            debugCanvas.style.display = 'none';
            console.log('Debug mode disabled');
        }
    });
    
    // Handle local play button
    playVsLocalBtn.addEventListener('click', () => {
        // Start local game
        startLocalGame();
    });

    // Handle online play button (multiplayer)
    let connection = null;

    playVsPlayerBtn.addEventListener('click', async () => {
        // Initialize SignalR connection
        if (connection === null) {
            connectionStatus.textContent = 'Connecting to server...';
            
            try {
                // Create SignalR connection
                const serverUrl = '/api'; // for Azure Functions
                
                connection = new signalR.HubConnectionBuilder()
                    .withUrl(`${serverUrl}/pong`)
                    .configureLogging(signalR.LogLevel.Information)
                    .build();
                
                // Handle connection events
                connection.onclose(() => {
                    connectionStatus.textContent = 'Disconnected';
                    connectionStatus.style.backgroundColor = '#d32f2f';
                });
                
                // Start the connection
                await connection.start();
                connectionStatus.textContent = 'Connected to server, waiting for opponent...';
                connectionStatus.style.backgroundColor = '#4caf50';
                
                // Register for game events
                setupGameEvents(connection);
                
                // Join multiplayer queue
                connection.invoke('JoinQueue');
                
            } catch (err) {
                console.error('Connection failed: ', err);
                connectionStatus.textContent = 'Connection failed: ' + err;
                connectionStatus.style.backgroundColor = '#d32f2f';
            }
        } else if (connection.state === 'Connected') {
            // Already connected, just join queue
            connectionStatus.textContent = 'Connected, waiting for opponent...';
            connection.invoke('JoinQueue');
        }
    });
    
    playVsBotBtn.addEventListener('click', async () => {
        // Similar to multiplayer but against a bot
        if (connection === null) {
            connectionStatus.textContent = 'Connecting to server...';
            
            try {
                // Create SignalR connection
                const serverUrl = '/api'; // for Azure Functions
                
                connection = new signalR.HubConnectionBuilder()
                    .withUrl(`${serverUrl}/pong`)
                    .configureLogging(signalR.LogLevel.Information)
                    .build();
                
                // Handle connection events
                connection.onclose(() => {
                    connectionStatus.textContent = 'Disconnected';
                    connectionStatus.style.backgroundColor = '#d32f2f';
                });
                
                // Start the connection
                await connection.start();
                connectionStatus.textContent = 'Connected to server, starting bot game...';
                connectionStatus.style.backgroundColor = '#4caf50';
                
                // Register for game events
                setupGameEvents(connection);
                
                // Join bot game
                connection.invoke('PlayAgainstBot');
                
            } catch (err) {
                console.error('Connection failed: ', err);
                connectionStatus.textContent = 'Connection failed: ' + err;
                connectionStatus.style.backgroundColor = '#d32f2f';
            }
        } else if (connection.state === 'Connected') {
            // Already connected, just start bot game
            connectionStatus.textContent = 'Connected, starting bot game...';
            connection.invoke('PlayAgainstBot');
        }
    });
    
    // Setup game events with SignalR connection
    function setupGameEvents(connection) {
        
        // Track ping times
        let pingInterval;
        let pingStartTime;
        
        // Start tracking ping on connect
        pingInterval = setInterval(() => {
            pingStartTime = Date.now();
            connection.invoke('Ping');
        }, 2000); // Check ping every 2 seconds
        
        // Handle receiving pong response for ping
        connection.on('Pong', () => {
            const pingTime = Date.now() - pingStartTime;
            updatePing(pingTime);
        });
        
        // Game events
        connection.on('GameStart', (playerNum) => {
            connectionStatus.textContent = `Game started! You are Player ${playerNum}`;
            
            // 1 = left, 2 = right
            const side = playerNum === 1 ? 1 : 2;
            
            // Send paddle updates function we'll pass to the game engine
            const sendPaddleUpdate = (y) => {
                connection.invoke('UpdatePaddle', y);
            };
            
            // Start multiplayer mode
            enableMultiplayer(side, sendPaddleUpdate);
        });
        
        connection.on('OpponentPaddle', (y) => {
            handleOpponentInput(y);
        });
        
        connection.on('GameState', (state) => {
            renderMultiplayerState(state);
            
            // Debug: track the last collision info from server
            const debugCollisionElement = document.getElementById('collision-check');
            if (debugCollisionElement && state.lastCollision) {
                const collisionResult = state.lastCollision.result ? 'HIT' : 'MISS';
                debugCollisionElement.textContent = `Server: ${collisionResult} at ${state.lastCollision.ballX},${state.lastCollision.ballY}`;
            }
        });
        
        connection.on('GameOver', (winner) => {
            connectionStatus.textContent = `Game Over! ${winner === 1 ? 'Left' : 'Right'} player wins`;
            
            // Clean up ping interval
            clearInterval(pingInterval);
        });
    }
});