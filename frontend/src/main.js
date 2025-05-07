// Main entry point for the application
import { initGame, startLocalGame, enableMultiplayer, renderMultiplayerState, handleOpponentInput, updatePing } from './game/game.js';
import { SIGNALR_HUB_URL } from './config.js';

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
                // Create SignalR connection using config URL
                connection = new signalR.HubConnectionBuilder()
                    .withUrl(SIGNALR_HUB_URL)
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
                connection.invoke('JoinMatchmaking');
                
            } catch (err) {
                console.error('Connection failed: ', err);
                connectionStatus.textContent = 'Connection failed: ' + err;
                connectionStatus.style.backgroundColor = '#d32f2f';
            }
        } else if (connection.state === 'Connected') {
            // Already connected, just join queue
            connectionStatus.textContent = 'Connected, waiting for opponent...';
            connection.invoke('JoinMatchmaking');
        }
    });
    
    playVsBotBtn.addEventListener('click', async () => {
        // Similar to multiplayer but against a bot
        if (connection === null) {
            connectionStatus.textContent = 'Connecting to server...';
            
            try {
                // Create SignalR connection using config URL
                connection = new signalR.HubConnectionBuilder()
                    .withUrl(SIGNALR_HUB_URL)
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
                connection.invoke('StartBotMatch');
                
            } catch (err) {
                console.error('Connection failed: ', err);
                connectionStatus.textContent = 'Connection failed: ' + err;
                connectionStatus.style.backgroundColor = '#d32f2f';
            }
        } else if (connection.state === 'Connected') {
            // Already connected, just start bot game
            connectionStatus.textContent = 'Connected, starting bot game...';
            connection.invoke('StartBotMatch');
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
            connection.invoke('KeepAlive');
        }, 2000); // Check ping every 2 seconds
        
        // Handle receiving pong response for ping
        connection.on('Pong', () => {
            const pingTime = Date.now() - pingStartTime;
            updatePing(pingTime);
        });
        
        // Game events
        connection.on('MatchFound', (data) => {
            connectionStatus.textContent = `Game found! You are Player ${data.side}`;
            
            // 1 = left, 2 = right
            const side = data.side;
            
            // Send paddle updates function we'll pass to the game engine
            const sendPaddleUpdate = (y) => {
                connection.invoke('SendPaddleInput', y);
            };
            
            // Start multiplayer mode
            enableMultiplayer(side, sendPaddleUpdate);
            
            // Signal ready to start
            connection.invoke('RequestStartGame');
        });
        
        connection.on('OpponentPaddleInput', (y) => {
            handleOpponentInput(y);
        });
        
        connection.on('GameUpdate', (state) => {
            renderMultiplayerState(state);
        });
        
        connection.on('OpponentDisconnected', (state) => {
            connectionStatus.textContent = `Opponent disconnected! Game Over.`;
            renderMultiplayerState(state);
            clearInterval(pingInterval);
        });
        
        connection.on('WaitingForOpponent', () => {
            connectionStatus.textContent = 'Waiting for an opponent...';
        });
        
        connection.on('AlreadyInGame', () => {
            connectionStatus.textContent = 'Already in an active game!';
        });
    }
});