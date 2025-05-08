// game.js - Enhanced with debugging features

// Game constants
const CANVAS_WIDTH = 800;
const CANVAS_HEIGHT = 600;
const PADDLE_WIDTH = 16;
const PADDLE_HEIGHT = 100;
const BALL_SIZE = 16;
const PADDLE_SPEED = 6;
const BALL_SPEED = 6;
const BOT_PADDLE_SPEED_FACTOR = 0.85; // Match server-side value from GameEngine.cs

// Game state variables
let playerY = (CANVAS_HEIGHT - PADDLE_HEIGHT) / 2;
let opponentY = (CANVAS_HEIGHT - PADDLE_HEIGHT) / 2;
let ballX = (CANVAS_WIDTH - BALL_SIZE) / 2;
let ballY = (CANVAS_HEIGHT - BALL_SIZE) / 2;
let ballVX = BALL_SPEED;
let ballVY = BALL_SPEED;
let playerScore = 0;
let opponentScore = 0;
let upPressed = false;
let downPressed = false;
let gameOver = false;

// Multiplayer variables
let isMultiplayer = false;
let playerSide = 1; // 1 = left, 2 = right
let sendPaddleUpdate = null;
let isGameActive = false;

// Server authoritative state for interpolation
let serverAuthoritativeBallX = (CANVAS_WIDTH - BALL_SIZE) / 2;
let serverAuthoritativeBallY = (CANVAS_HEIGHT - BALL_SIZE) / 2;
let serverAuthoritativeBallVX = BALL_SPEED; // Initial assumption, will be updated by server
let serverAuthoritativeBallVY = BALL_SPEED; // Initial assumption, will be updated by server
let newServerUpdateProcessed = true; // Tracks if the latest server velocities have been applied

// Debug variables
let lastServerUpdate = Date.now();
let correctionCount = 0;
let pingMs = 0;
let lastFrameTime = 0;
let fpsCounter = 0;
let frameCount = 0;
const FPS_UPDATE_INTERVAL = 500; // Update FPS counter every 500ms

// Server-reported paddle positions for comparison
let serverLeftPaddleY = null;
let serverRightPaddleY = null;

// Debug history tracking
let paddleHistory = [];
let ballHistory = [];
let goalHistory = [];
let collisionChecks = [];
const MAX_HISTORY_LENGTH = 1000;
let visualDebugEnabled = false;
let lastCollisionCheck = { time: 0, result: false, ballX: 0, ballY: 0, paddleY: 0 };

// Reset ball to center with random direction
function resetBall() {
    ballX = (CANVAS_WIDTH - BALL_SIZE) / 2;
    ballY = (CANVAS_HEIGHT - BALL_SIZE) / 2;
    ballVX = BALL_SPEED * (Math.random() > 0.5 ? 1 : -1);
    ballVY = BALL_SPEED * (Math.random() > 0.5 ? 1 : -1);
}

// Record data about a goal for debugging
function recordGoalData() {
    // Don't record if game just started
    if (ballX === (CANVAS_WIDTH - BALL_SIZE) / 2 && ballY === (CANVAS_HEIGHT - BALL_SIZE) / 2) return;
    
    const timestamp = new Date().toISOString();
    // Capture server paddle Y for the relevant side (if available)
    let serverPaddleY = null;
    if (isMultiplayer) {
        if (playerSide === 1) {
            serverPaddleY = serverLeftPaddleY;
        } else {
            serverPaddleY = serverRightPaddleY;
        }
    }
    const goalData = {
        timestamp,
        scorer: ballX < 0 ? 'right' : 'left',
        ballX,
        ballY,
        ballVX,
        ballVY,
        playerY, // frontend paddle Y
        serverPaddleY, // backend paddle Y (last known)
        opponentY,
        playerSide,
        // Include recent history from last 50 frames
        ballPath: ballHistory.slice(-50),
        paddlePath: paddleHistory.slice(-50),
        recentCollisionChecks: collisionChecks.slice(-10)
    };
    
    goalHistory.push(goalData);
    if (goalHistory.length > 20) goalHistory.shift(); // Keep last 20 goals
    
    // Update goal history display
    updateGoalHistoryDisplay();
}

// Draw the game state
function draw(ctx) {
    // Clear canvas
    ctx.clearRect(0, 0, CANVAS_WIDTH, CANVAS_HEIGHT);
    ctx.fillStyle = '#222';
    ctx.fillRect(0, 0, CANVAS_WIDTH, CANVAS_HEIGHT);
    
    // Draw center line
    ctx.strokeStyle = '#444';
    ctx.setLineDash([10, 10]);
    ctx.beginPath();
    ctx.moveTo(CANVAS_WIDTH / 2, 0);
    ctx.lineTo(CANVAS_WIDTH / 2, CANVAS_HEIGHT);
    ctx.stroke();
    ctx.setLineDash([]);
    
    // Draw paddles - green for player, white for opponent
    if (playerSide === 1) {
        // Player on left
        ctx.fillStyle = '#4caf50';
        ctx.fillRect(16, playerY, PADDLE_WIDTH, PADDLE_HEIGHT);
        ctx.fillStyle = '#fff';
        ctx.fillRect(CANVAS_WIDTH - 32, opponentY, PADDLE_WIDTH, PADDLE_HEIGHT);
    } else {
        // Player on right
        ctx.fillStyle = '#fff';
        ctx.fillRect(16, opponentY, PADDLE_WIDTH, PADDLE_HEIGHT);
        ctx.fillStyle = '#4caf50';
        ctx.fillRect(CANVAS_WIDTH - 32, playerY, PADDLE_WIDTH, PADDLE_HEIGHT);
    }
    
    // Draw ball
    ctx.fillStyle = '#fff';
    ctx.fillRect(ballX, ballY, BALL_SIZE, BALL_SIZE);
    
    // Draw scores
    ctx.font = '48px monospace';
    ctx.textAlign = 'center';
    ctx.fillStyle = '#fff';
    const leftScore = playerSide === 1 ? playerScore : opponentScore;
    const rightScore = playerSide === 1 ? opponentScore : playerScore;
    ctx.fillText(leftScore, CANVAS_WIDTH / 2 - 50, 60);
    ctx.fillText(rightScore, CANVAS_WIDTH / 2 + 50, 60);
    
    // Game over message
    if (gameOver) {
        ctx.fillStyle = 'rgba(0, 0, 0, 0.7)';
        ctx.fillRect(CANVAS_WIDTH / 2 - 200, CANVAS_HEIGHT / 2 - 40, 400, 80);
        ctx.font = '36px monospace';
        ctx.fillStyle = '#fff';
        ctx.fillText('Game Over!', CANVAS_WIDTH / 2, CANVAS_HEIGHT / 2);
        ctx.font = '18px monospace';
        ctx.fillText('Press R to Restart', CANVAS_WIDTH / 2, CANVAS_HEIGHT / 2 + 30);
    }
    
    // Draw visual debug elements if enabled
    if (visualDebugEnabled) {
        drawDebugOverlay();
    }
    
    // Update FPS counter
    frameCount++;
    const now = Date.now();
    if (now - lastFrameTime >= FPS_UPDATE_INTERVAL) {
        fpsCounter = Math.round((frameCount * 1000) / (now - lastFrameTime));
        frameCount = 0;
        lastFrameTime = now;
        
        // Update debug info if debug panel is visible
        updateDebugDisplay();
    }
    
    // Record history for debugging
    recordPositionHistory();
}

// Draw visual debug overlay
function drawDebugOverlay() {
    const debugCanvas = document.getElementById('debug-canvas');
    if (!debugCanvas) return;
    
    const dctx = debugCanvas.getContext('2d');
    dctx.clearRect(0, 0, CANVAS_WIDTH, CANVAS_HEIGHT);
    
    // Draw ball trajectory prediction
    dctx.strokeStyle = 'rgba(255, 255, 0, 0.5)';
    dctx.beginPath();
    dctx.moveTo(ballX + BALL_SIZE/2, ballY + BALL_SIZE/2);
    
    // Simple prediction - extend current velocity
    let predX = ballX + BALL_SIZE/2;
    let predY = ballY + BALL_SIZE/2;
    let predVX = ballVX;
    let predVY = ballVY;
    
    for (let i = 0; i < 60; i++) {
        predX += predVX;
        predY += predVY;
        
        // Reflect off top/bottom
        if (predY <= 0 || predY >= CANVAS_HEIGHT) {
            predVY = -predVY;
        }
        
        dctx.lineTo(predX, predY);
    }
    dctx.stroke();
    
    // Draw ball history path
    dctx.strokeStyle = 'rgba(255, 0, 255, 0.5)';
    dctx.beginPath();
    if (ballHistory.length > 1) {
        dctx.moveTo(ballHistory[0].x + BALL_SIZE/2, ballHistory[0].y + BALL_SIZE/2);
        for (let i = 1; i < ballHistory.length; i++) {
            dctx.lineTo(ballHistory[i].x + BALL_SIZE/2, ballHistory[i].y + BALL_SIZE/2);
        }
    }
    dctx.stroke();
    
    // Highlight paddle collision areas
    const leftPaddleX = 16;
    const rightPaddleX = CANVAS_WIDTH - 32;
    
    // Visualize the server-side collision buffer (4 pixels)
    const COLLISION_BUFFER = 4;
    
    // Player's paddle collision area
    if (playerSide === 1) {
        // Normal paddle
        dctx.strokeStyle = 'rgba(0, 255, 0, 0.8)';
        dctx.strokeRect(leftPaddleX, playerY, PADDLE_WIDTH, PADDLE_HEIGHT);
        
        // Extended hitbox - shows what the server is actually checking against
        dctx.strokeStyle = 'rgba(255, 255, 0, 0.5)';
        dctx.strokeRect(
            leftPaddleX - COLLISION_BUFFER/2, 
            playerY - COLLISION_BUFFER, 
            PADDLE_WIDTH + COLLISION_BUFFER, 
            PADDLE_HEIGHT + COLLISION_BUFFER * 2
        );
    } else {
        // Normal paddle
        dctx.strokeStyle = 'rgba(0, 255, 0, 0.8)';
        dctx.strokeRect(rightPaddleX, playerY, PADDLE_WIDTH, PADDLE_HEIGHT);
        
        // Extended hitbox - shows what the server is actually checking against
        dctx.strokeStyle = 'rgba(255, 255, 0, 0.5)';
        dctx.strokeRect(
            rightPaddleX - BALL_SIZE - COLLISION_BUFFER/2, 
            playerY - COLLISION_BUFFER, 
            PADDLE_WIDTH + COLLISION_BUFFER, 
            PADDLE_HEIGHT + COLLISION_BUFFER * 2
        );
    }
    
    // Draw last collision check
    if (lastCollisionCheck.time > Date.now() - 1000) {
        dctx.fillStyle = lastCollisionCheck.result ? 'rgba(0, 255, 0, 0.5)' : 'rgba(255, 0, 0, 0.5)';
        dctx.beginPath();
        dctx.arc(lastCollisionCheck.ballX + BALL_SIZE/2, lastCollisionCheck.ballY + BALL_SIZE/2, BALL_SIZE, 0, Math.PI * 2);
        dctx.fill();
    }
}

// Record current positions for debugging
function recordPositionHistory() {
    const now = Date.now();
    
    // Record ball position (client's visual position)
    ballHistory.push({
        time: now,
        x: ballX,
        y: ballY,
        vx: ballVX,
        vy: ballVY
    });
    
    // Record paddle positions (both player and opponent)
    paddleHistory.push({
        time: now,
        playerY: playerY,
        opponentY: opponentY,
        side: playerSide
    });
    
    // Trim history if too long
    if (ballHistory.length > MAX_HISTORY_LENGTH) ballHistory.shift();
    if (paddleHistory.length > MAX_HISTORY_LENGTH) paddleHistory.shift();
}

// Update debug information display
function updateDebugDisplay() {
    const ballPosElem = document.getElementById('ball-position');
    const ballVelElem = document.getElementById('ball-velocity');
    const paddlePosElem = document.getElementById('paddle-position');
    const collisionCheckElem = document.getElementById('collision-check');
    const fpsElement = document.getElementById('fps-counter');
    const pingElement = document.getElementById('ping-counter');
    const correctionElement = document.getElementById('correction-counter');
    
    if (ballPosElem) ballPosElem.textContent = `${Math.round(ballX)},${Math.round(ballY)}`;
    if (ballVelElem) ballVelElem.textContent = `${ballVX.toFixed(2)},${ballVY.toFixed(2)}`;
    if (paddlePosElem) paddlePosElem.textContent = `${Math.round(playerY)}`;
    
    // Format collision check info
    if (collisionCheckElem && lastCollisionCheck) {
        const timeSince = Math.round((Date.now() - lastCollisionCheck.time) / 1000);
        collisionCheckElem.textContent = `${timeSince}s ago - ${lastCollisionCheck.result ? 'HIT' : 'MISS'}`;
    }
    
    if (fpsElement) fpsElement.textContent = fpsCounter;
    if (pingElement) pingElement.textContent = pingMs;
    if (correctionElement) correctionElement.textContent = correctionCount;
}

// Update goal history display in debug panel
function updateGoalHistoryDisplay() {
    const goalHistoryElem = document.getElementById('goal-history');
    if (!goalHistoryElem) return;
    
    // Clear previous entries
    goalHistoryElem.innerHTML = '';
    
    // Add each goal as a clickable item
    goalHistory.forEach((goal, index) => {
        const goalEntry = document.createElement('div');
        goalEntry.className = 'goal-entry';
        
        const scorer = goal.scorer === 'left' ? 
            (playerSide === 1 ? 'You' : 'Opponent') : 
            (playerSide === 2 ? 'You' : 'Opponent');
        
        // Show both frontend and backend paddle Y in the tooltip
        let paddleInfo = `Frontend: ${Math.round(goal.playerY)}`;
        if (goal.serverPaddleY !== undefined && goal.serverPaddleY !== null) {
            paddleInfo += ` | Backend: ${Math.round(goal.serverPaddleY)}`;
        }
        
        goalEntry.innerHTML = `<span>${index+1}. ${scorer} scored at ${goal.timestamp.split('T')[1].slice(0,8)}</span>`;
        goalEntry.title = `Ball: ${Math.round(goal.ballX)},${Math.round(goal.ballY)} - Paddle Y: ${paddleInfo}`;
        
        // Make entry clickable to show detailed replay data
        goalEntry.addEventListener('click', () => {
            let details = `Goal ${index+1} Details:\nScorer: ${scorer}\nBall Position: ${Math.round(goal.ballX)},${Math.round(goal.ballY)}\nBall Velocity: ${goal.ballVX.toFixed(2)},${goal.ballVY.toFixed(2)}\nPaddle Y (Frontend): ${Math.round(goal.playerY)}`;
            if (goal.serverPaddleY !== undefined && goal.serverPaddleY !== null) {
                details += `\nPaddle Y (Backend): ${Math.round(goal.serverPaddleY)}`;
                details += `\nDelta: ${Math.abs(Math.round(goal.playerY) - Math.round(goal.serverPaddleY))}`;
            }
            alert(details);
        });
        
        goalHistoryElem.appendChild(goalEntry);
    });
}

// Update game state for single player mode
function updateSinglePlayer() {
    if (gameOver) return;
    
    // Player paddle movement
    if (upPressed) playerY -= PADDLE_SPEED;
    if (downPressed) playerY += PADDLE_SPEED;
    playerY = Math.max(0, Math.min(CANVAS_HEIGHT - PADDLE_HEIGHT, playerY));
    
    // Simple AI for opponent
    const paddleCenter = opponentY + PADDLE_HEIGHT / 2;
    const ballCenter = ballY + BALL_SIZE / 2;
    
    if (paddleCenter < ballCenter - 10) {
        opponentY += PADDLE_SPEED * BOT_PADDLE_SPEED_FACTOR; // Using the server-synchronized factor
    } else if (paddleCenter > ballCenter + 10) {
        opponentY -= PADDLE_SPEED * BOT_PADDLE_SPEED_FACTOR;
    }
    opponentY = Math.max(0, Math.min(CANVAS_HEIGHT - PADDLE_HEIGHT, opponentY));
    
    // Ball movement
    ballX += ballVX;
    ballY += ballVY;
    
    // Wall collisions
    if (ballY <= 0 || ballY + BALL_SIZE >= CANVAS_HEIGHT) {
        ballVY = -ballVY;
        ballY = Math.max(0, Math.min(CANVAS_HEIGHT - BALL_SIZE, ballY));
    }
    
    // Paddle collisions
    const leftPaddleX = 16 + PADDLE_WIDTH;
    const rightPaddleX = CANVAS_WIDTH - 32;
    
    // Left paddle collision
    if (ballX <= leftPaddleX && 
        ballX + ballVX <= leftPaddleX &&
        ballY + BALL_SIZE >= (playerSide === 1 ? playerY : opponentY) && 
        ballY <= (playerSide === 1 ? playerY : opponentY) + PADDLE_HEIGHT) {
        
        // Record collision for debugging
        lastCollisionCheck = {
            time: Date.now(),
            result: true,
            ballX: ballX,
            ballY: ballY,
            paddleY: (playerSide === 1 ? playerY : opponentY)
        };
        collisionChecks.push({...lastCollisionCheck});
        if (collisionChecks.length > 100) collisionChecks.shift();
        
        ballVX = Math.abs(ballVX); // Reverse direction
        
        // Add some angle based on where ball hits paddle
        const hitPos = (ballY + BALL_SIZE/2) - ((playerSide === 1 ? playerY : opponentY) + PADDLE_HEIGHT/2);
        ballVY = hitPos * 0.2; // Convert to reasonable velocity
    }
    
    // Right paddle collision
    if (ballX + BALL_SIZE >= rightPaddleX && 
        ballX + BALL_SIZE + ballVX >= rightPaddleX &&
        ballY + BALL_SIZE >= (playerSide === 2 ? playerY : opponentY) && 
        ballY <= (playerSide === 2 ? playerY : opponentY) + PADDLE_HEIGHT) {
        
        // Record collision for debugging
        lastCollisionCheck = {
            time: Date.now(),
            result: true,
            ballX: ballX,
            ballY: ballY,
            paddleY: (playerSide === 2 ? playerY : opponentY)
        };
        collisionChecks.push({...lastCollisionCheck});
        if (collisionChecks.length > 100) collisionChecks.shift();
        
        ballVX = -Math.abs(ballVX); // Reverse direction
        
        // Add some angle based on where ball hits paddle
        const hitPos = (ballY + BALL_SIZE/2) - ((playerSide === 2 ? playerY : opponentY) + PADDLE_HEIGHT/2);
        ballVY = hitPos * 0.2; // Convert to reasonable velocity
    }
    
    // Check for near-misses (for debugging)
    checkNearMisses();
    
    // Scoring
    if (ballX < 0) {
        // Right scores
        recordGoalData();
        if (playerSide === 2) playerScore++; else opponentScore++;
        if (Math.max(playerScore, opponentScore) >= 5) gameOver = true;
        resetBall();
    } else if (ballX > CANVAS_WIDTH) {
        // Left scores
        recordGoalData();
        if (playerSide === 1) playerScore++; else opponentScore++;
        if (Math.max(playerScore, opponentScore) >= 5) gameOver = true;
        resetBall();
    }
}

// Check for near-misses with paddles (for debugging)
function checkNearMisses() {
    const ballCenterX = ballX + BALL_SIZE/2;
    const ballCenterY = ballY + BALL_SIZE/2;
    
    // Check left paddle near miss
    if (ballVX < 0 && ballCenterX < 50 && ballCenterX > 10) {
        const leftPaddleY = playerSide === 1 ? playerY : opponentY;
        const distance = Math.min(
            Math.abs(ballCenterY - leftPaddleY),
            Math.abs(ballCenterY - (leftPaddleY + PADDLE_HEIGHT))
        );
        
        // If ball is close to paddle but missing it
        if (distance < 30 && distance > 0 && 
            (ballCenterY < leftPaddleY || ballCenterY > leftPaddleY + PADDLE_HEIGHT)) {
            
            // Record near miss
            lastCollisionCheck = {
                time: Date.now(),
                result: false,
                ballX: ballX,
                ballY: ballY,
                paddleY: leftPaddleY,
                nearMiss: true,
                distance
            };
            collisionChecks.push({...lastCollisionCheck});
            if (collisionChecks.length > 100) collisionChecks.shift();
        }
    }
    
    // Check right paddle near miss
    if (ballVX > 0 && ballCenterX > CANVAS_WIDTH - 50 && ballCenterX < CANVAS_WIDTH - 10) {
        const rightPaddleY = playerSide === 2 ? playerY : opponentY;
        const distance = Math.min(
            Math.abs(ballCenterY - rightPaddleY),
            Math.abs(ballCenterY - (rightPaddleY + PADDLE_HEIGHT))
        );
        
        // If ball is close to paddle but missing it
        if (distance < 30 && distance > 0 && 
            (ballCenterY < rightPaddleY || ballCenterY > rightPaddleY + PADDLE_HEIGHT)) {
            
            // Record near miss
            lastCollisionCheck = {
                time: Date.now(),
                result: false,
                ballX: ballX,
                ballY: ballY,
                paddleY: rightPaddleY,
                nearMiss: true,
                distance
            };
            collisionChecks.push({...lastCollisionCheck});
            if (collisionChecks.length > 100) collisionChecks.shift();
        }
    }
}

// Update multiplayer game state
function updateMultiplayer() {
    if (gameOver) return;
    
    // Player paddle movement - directly controlled by input
    if (upPressed) playerY -= PADDLE_SPEED;
    if (downPressed) playerY += PADDLE_SPEED;
    playerY = Math.max(0, Math.min(CANVAS_HEIGHT - PADDLE_HEIGHT, playerY));
    
    // Send paddle position to server periodically
    const now = Date.now();
    if (now - lastPaddleUpdateTime > 50 && sendPaddleUpdate) { // ~20 updates per second
        sendPaddleUpdate(playerY);
        lastPaddleUpdateTime = now;
    }
}

// Game loop for single player
function gameLoop(ctx) {
    if (!isGameActive) return;
    
    updateSinglePlayer();
    draw(ctx);
    
    if (!isMultiplayer && isGameActive) {
        requestAnimationFrame(() => gameLoop(ctx));
    }
}

// Game loop for multiplayer
function gameLoopMultiplayer(ctx) {
    if (!isGameActive) return;
    
    updateMultiplayer(); // Handles local player paddle movement and sending updates
    
    // Client-side prediction for the ball
    // Assuming 60fps, clientDeltaTime is approx 1/60.
    // Server multiplies velocity by deltaTime * 60, so we do the same here.
    const clientPredictionDelta = 1 / 60; 
    ballX += ballVX * clientPredictionDelta * 60;
    ballY += ballVY * clientPredictionDelta * 60;

    // Client-side wall collision prediction
    if (ballY <= 0 || ballY + BALL_SIZE >= CANVAS_HEIGHT) {
        ballVY = -ballVY;
        ballY = Math.max(0, Math.min(CANVAS_HEIGHT - BALL_SIZE, ballY)); // Clamp to bounds
    }
    // Note: Client-side prediction of paddle collisions is complex and usually omitted
    // to rely on the server's authoritative collision detection.

    // Server Reconciliation: Interpolate visual ball towards server's authoritative position
    const interpolationFactor = 0.15; // Adjust for smoothness (0.1 - 0.3 typically)
    ballX += (serverAuthoritativeBallX - ballX) * interpolationFactor;
    ballY += (serverAuthoritativeBallY - ballY) * interpolationFactor;

    // If a new server update has provided fresh velocities, adopt them.
    if (!newServerUpdateProcessed) {
        ballVX = serverAuthoritativeBallVX;
        ballVY = serverAuthoritativeBallVY;
        newServerUpdateProcessed = true; // Mark that these new velocities are now in use by client prediction
    }

    // Snap to authoritative position if very close, to prevent micro-oscillations
    if (Math.abs(serverAuthoritativeBallX - ballX) < 0.5) {
        ballX = serverAuthoritativeBallX;
    }
    if (Math.abs(serverAuthoritativeBallY - ballY) < 0.5) {
        ballY = serverAuthoritativeBallY;
    }
    
    draw(ctx);
    
    if (isMultiplayer && isGameActive) {
        requestAnimationFrame(() => gameLoopMultiplayer(ctx));
    }
}

// Process the game state received from server
function renderServerState(state) {
    if (!state || typeof state !== 'object') return;
    
    // Record timing for debug info
    lastServerUpdate = Date.now();
    
    // Update authoritative state from server
    serverAuthoritativeBallX = state.ball?.x ?? serverAuthoritativeBallX;
    serverAuthoritativeBallY = state.ball?.y ?? serverAuthoritativeBallY;
    serverAuthoritativeBallVX = state.ball?.velocityX ?? serverAuthoritativeBallVX;
    serverAuthoritativeBallVY = state.ball?.velocityY ?? serverAuthoritativeBallVY;
    newServerUpdateProcessed = false; // Flag that new server velocities are available for the client to adopt

    // Calculate how much our prediction deviated (for debugging)
    const ballDeviation = Math.sqrt(
        Math.pow(serverAuthoritativeBallX - ballX, 2) + 
        Math.pow(serverAuthoritativeBallY - ballY, 2)
    );
    
    // If the ball position is significantly different, count as a correction
    if (ballDeviation > 10) { // Threshold for "significant" deviation
        correctionCount++;
    }
    
    // Record collision details if server reports one
    if (state.lastCollision && state.lastCollision.time > 0) {
        lastCollisionCheck = {
            time: Date.now(),
            result: true,
            ballX: state.lastCollision.ballX,
            ballY: state.lastCollision.ballY,
            paddleY: state.lastCollision.paddleY,
            serverReported: true
        };
        collisionChecks.push({...lastCollisionCheck});
        if (collisionChecks.length > 100) collisionChecks.shift();
    }
    
    // Update game state from server
    if (playerSide === 1) {
        // We're on the left
        opponentY = state.rightPaddle?.y ?? opponentY;
        // Store server paddle positions for comparison
        serverLeftPaddleY = state.leftPaddle?.y ?? null;  
        serverRightPaddleY = state.rightPaddle?.y ?? null;
        // DO NOT directly set ballX, ballY here. Interpolation handles it.
    } else {
        // We're on the right
        opponentY = state.leftPaddle?.y ?? opponentY;
        // Store server paddle positions for comparison
        serverLeftPaddleY = state.leftPaddle?.y ?? null;
        serverRightPaddleY = state.rightPaddle?.y ?? null;
        // DO NOT directly set ballX, ballY here. Interpolation handles it.
    }
    
    // DO NOT directly set ballVX, ballVY here. The game loop handles adopting new server velocities.
    
    // Update scores
    if (playerSide === 1) {
        playerScore = state.leftScore ?? playerScore;
        opponentScore = state.rightScore ?? opponentScore;
    } else {
        playerScore = state.rightScore ?? playerScore;
        opponentScore = state.leftScore ?? opponentScore;
    }
    
    // Update game state
    gameOver = state.gameOver ?? gameOver;
}

// Handle keyboard input
let lastPaddleUpdateTime = 0;

function handleKeyDown(e) {
    if (e.key === 'ArrowUp' || e.key === 'w') upPressed = true;
    if (e.key === 'ArrowDown' || e.key === 's') downPressed = true;
    if (e.key === 'r' && gameOver) {
        playerScore = 0; 
        opponentScore = 0; 
        gameOver = false; 
        resetBall();
    }
}

function handleKeyUp(e) {
    if (e.key === 'ArrowUp' || e.key === 'w') upPressed = false;
    if (e.key === 'ArrowDown' || e.key === 's') downPressed = false;
}

// Start multiplayer game mode
export function enableMultiplayer(side, sendUpdateFn) {
    isMultiplayer = true;
    playerSide = side;
    sendPaddleUpdate = sendUpdateFn;
    isGameActive = true;
    
    // Reset game state (client's visual state)
    playerY = (CANVAS_HEIGHT - PADDLE_HEIGHT) / 2;
    opponentY = (CANVAS_HEIGHT - PADDLE_HEIGHT) / 2;
    resetBall(); // This sets client's ballX, ballY, ballVX, ballVY
    playerScore = 0;
    opponentScore = 0;
    gameOver = false;

    // Initialize server authoritative state to match client's initial state
    serverAuthoritativeBallX = ballX;
    serverAuthoritativeBallY = ballY;
    serverAuthoritativeBallVX = ballVX;
    serverAuthoritativeBallVY = ballVY;
    newServerUpdateProcessed = true; // Start as true, assuming initial state is "processed"
    
    // Clear debug history
    ballHistory = [];
    paddleHistory = [];
    collisionChecks = [];
    
    // Show instructions
    const instructions = document.getElementById('game-instructions');
    if (instructions) instructions.style.display = 'block';
    
    // Check if debug mode is enabled
    const debugCheckbox = document.getElementById('toggle-debug');
    if (debugCheckbox && debugCheckbox.checked) {
        const debugPanel = document.getElementById('debug-panel');
        if (debugPanel) debugPanel.style.display = 'block';
        
        // Enable visual debugging
        visualDebugEnabled = true;
        const debugCanvas = document.getElementById('debug-canvas');
        if (debugCanvas) debugCanvas.style.display = 'block';
    }
    
    // Set up download debug data button
    setupDebugDownload();
    
    // Get canvas context first
    const canvas = document.getElementById('pong-canvas');
    const currentCtx = canvas ? canvas.getContext('2d') : null;
    
    // Start animation loop
    if (currentCtx) {
        // Send initial paddle position
        if (sendPaddleUpdate) {
            sendPaddleUpdate(playerY);
        }
        lastFrameTime = Date.now();
        frameCount = 0;
        requestAnimationFrame(() => gameLoopMultiplayer(currentCtx));
    }
}

// Set up debug data download
function setupDebugDownload() {
    const downloadButton = document.getElementById('download-debug');
    if (downloadButton) {
        downloadButton.addEventListener('click', () => {
            const debugData = {
                timestamp: new Date().toISOString(),
                playerSide,
                goals: goalHistory,
                ballHistory: ballHistory.slice(-500), // Last 500 records
                paddleHistory: paddleHistory.slice(-500), // Last 500 records
                collisionChecks
            };
            
            // Create file for download
            const dataStr = JSON.stringify(debugData, null, 2);
            const dataBlob = new Blob([dataStr], {type: 'application/json'});
            const url = URL.createObjectURL(dataBlob);
            
            // Create download link
            const a = document.createElement('a');
            a.href = url;
            a.download = `pong-debug-${new Date().toISOString().slice(0,19).replace(/:/g,'-')}.json`;
            document.body.appendChild(a);
            a.click();
            
            // Cleanup
            setTimeout(() => {
                document.body.removeChild(a);
                URL.revokeObjectURL(url);
            }, 0);
        });
    }
}

// Handle opponent paddle updates (directly from other client)
export function handleOpponentInput(targetY) {
    if (playerSide === 1) {
        opponentY = targetY;
    } else {
        opponentY = targetY;
    }
}

// Update from server
export function renderMultiplayerState(state) {
    renderServerState(state);
}

// Record ping time
export function updatePing(pingTime) {
    pingMs = pingTime;
}

// Start local single-player game
export function startLocalGame() {
    isMultiplayer = false;
    isGameActive = true;
    playerSide = 1; // Always left in single player
    
    // Reset game state
    playerY = (CANVAS_HEIGHT - PADDLE_HEIGHT) / 2;
    opponentY = (CANVAS_HEIGHT - PADDLE_HEIGHT) / 2;
    resetBall();
    playerScore = 0;
    opponentScore = 0;
    gameOver = false;
    
    // Clear debug history (but do NOT clear goalHistory)
    ballHistory = [];
    paddleHistory = [];
    collisionChecks = [];
    
    // Show instructions
    const instructions = document.getElementById('game-instructions');
    if (instructions) {
        instructions.style.display = 'block';
    }
    
    // Check if debug mode is enabled
    const debugCheckbox = document.getElementById('toggle-debug');
    if (debugCheckbox && debugCheckbox.checked) {
        const debugPanel = document.getElementById('debug-panel');
        if (debugPanel) debugPanel.style.display = 'block';
        
        // Enable visual debugging
        visualDebugEnabled = true;
        const debugCanvas = document.getElementById('debug-canvas');
        if (debugCanvas) debugCanvas.style.display = 'block';
    }
    
    // Set up download debug data button
    setupDebugDownload();

    // Get canvas context first
    const canvas = document.getElementById('pong-canvas');
    currentCtx = canvas ? canvas.getContext('2d') : null;

    // Start animation loop
    if (currentCtx) {
        lastFrameTime = Date.now();
        frameCount = 0;
        requestAnimationFrame(() => gameLoop(currentCtx));
    }
}

// Track canvas context
let currentCtx = null;

// Initialize game
export function initGame() {
    const canvas = document.getElementById('pong-canvas');
    if (!canvas) return;
    
    const ctx = canvas.getContext('2d');
    currentCtx = ctx;
    
    // Set up input handlers
    document.addEventListener('keydown', handleKeyDown);
    document.addEventListener('keyup', handleKeyUp);
    
    // Set up debug toggle
    const debugCheckbox = document.getElementById('toggle-debug');
    if (debugCheckbox) {
        debugCheckbox.addEventListener('change', function() {
            const debugPanel = document.getElementById('debug-panel');
            if (debugPanel) {
                debugPanel.style.display = this.checked ? 'block' : 'none';
            }
            
            // Toggle visual debugging
            visualDebugEnabled = this.checked;
            const debugCanvas = document.getElementById('debug-canvas');
            if (debugCanvas) {
                debugCanvas.style.display = this.checked ? 'block' : 'none';
            }
        });
    }
    
    // Initial draw to show game board
    resetBall();
    draw(ctx);
    
    // Show instructions briefly at start
    const instructions = document.getElementById('game-instructions');
    if (instructions) {
        instructions.style.display = 'block';
        setTimeout(() => {
            instructions.style.display = 'none';
        }, 3000);
    }
}

// Clean up game resources
export function cleanupGame() {
    isGameActive = false;
    document.removeEventListener('keydown', handleKeyDown);
    document.removeEventListener('keyup', handleKeyUp);
}