// game.js

// Local single-player Pong game implementation

const CANVAS_WIDTH = 800;
const CANVAS_HEIGHT = 600;
const PADDLE_WIDTH = 16;
const PADDLE_HEIGHT = 100;
const BALL_SIZE = 16;
const PADDLE_SPEED = 6;
const BALL_SPEED = 6;

let playerY = (CANVAS_HEIGHT - PADDLE_HEIGHT) / 2;
let localPlayerY = (CANVAS_HEIGHT - PADDLE_HEIGHT) / 2;
let aiY = (CANVAS_HEIGHT - PADDLE_HEIGHT) / 2;
let ballX = (CANVAS_WIDTH - BALL_SIZE) / 2;
let ballY = (CANVAS_HEIGHT - BALL_SIZE) / 2;
let ballVX = BALL_SPEED, ballVY = BALL_SPEED;
let playerScore = 0, aiScore = 0;
let upPressed = false, downPressed = false;
let gameOver = false;

let isMultiplayer = false;
let multiplayerSide = 1; // 1 = left, 2 = right
let sendPaddleUpdate = null;
let isGameActive = false;

function resetBall() {
    ballX = (CANVAS_WIDTH - BALL_SIZE) / 2;
    ballY = (CANVAS_HEIGHT - BALL_SIZE) / 2;
    ballVX = BALL_SPEED * (Math.random() > 0.5 ? 1 : -1);
    ballVY = BALL_SPEED * (Math.random() > 0.5 ? 1 : -1);
}

function draw(ctx) {
    ctx.clearRect(0, 0, CANVAS_WIDTH, CANVAS_HEIGHT);
    ctx.fillStyle = '#222';
    ctx.fillRect(0, 0, CANVAS_WIDTH, CANVAS_HEIGHT);
    // Draw paddles with color indicating local vs opponent
    if (isMultiplayer) {
        if (multiplayerSide === 1) {
            // Local player left, opponent right
            ctx.fillStyle = '#4caf50'; // Local paddle: green
            ctx.fillRect(16, playerY, PADDLE_WIDTH, PADDLE_HEIGHT);
            ctx.fillStyle = '#fff'; // Opponent paddle: white
            ctx.fillRect(CANVAS_WIDTH - 32, aiY, PADDLE_WIDTH, PADDLE_HEIGHT);
        } else {
            // Local player right, opponent left
            ctx.fillStyle = '#fff'; // Opponent paddle: white
            ctx.fillRect(16, aiY, PADDLE_WIDTH, PADDLE_HEIGHT);
            ctx.fillStyle = '#4caf50'; // Local paddle: green
            ctx.fillRect(CANVAS_WIDTH - 32, playerY, PADDLE_WIDTH, PADDLE_HEIGHT);
        }
    } else {
        // Single player: always left
        ctx.fillStyle = '#4caf50';
        ctx.fillRect(16, playerY, PADDLE_WIDTH, PADDLE_HEIGHT);
        ctx.fillStyle = '#fff';
        ctx.fillRect(CANVAS_WIDTH - 32, aiY, PADDLE_WIDTH, PADDLE_HEIGHT);
    }
    // Draw ball
    ctx.fillStyle = '#fff';
    ctx.fillRect(ballX, ballY, BALL_SIZE, BALL_SIZE);
    // Draw scores
    ctx.font = '48px monospace';
    ctx.textAlign = 'center';
    ctx.fillStyle = '#fff';
    ctx.fillText(playerScore, CANVAS_WIDTH / 2 - 50, 60);
    ctx.fillText(aiScore, CANVAS_WIDTH / 2 + 50, 60);
    // Game over
    if (gameOver) {
        ctx.font = '36px monospace';
        ctx.fillText('Game Over! Press R to Restart', CANVAS_WIDTH / 2, CANVAS_HEIGHT / 2);
    }
}

function update() {
    if (gameOver) {
        // Stop the multiplayer interval if game is over
        if (isMultiplayer && window.multiplayerInterval) {
            clearInterval(window.multiplayerInterval);
            window.multiplayerInterval = null;
        }
        return;
    }
    
    // Player paddle movement
    if (isMultiplayer) {
        let oldPlayerY = localPlayerY;
        
        if (upPressed) localPlayerY -= PADDLE_SPEED;
        if (downPressed) localPlayerY += PADDLE_SPEED;
        localPlayerY = Math.max(0, Math.min(CANVAS_HEIGHT - PADDLE_HEIGHT, localPlayerY));
        playerY = localPlayerY;
        
        // Only send paddle updates if the position actually changed
        if (!gameOver && sendPaddleUpdate && Math.abs(oldPlayerY - localPlayerY) > 0) {
            sendPaddleUpdate(localPlayerY);
        }
    } else {
        if (upPressed) playerY -= PADDLE_SPEED;
        if (downPressed) playerY += PADDLE_SPEED;
        playerY = Math.max(0, Math.min(CANVAS_HEIGHT - PADDLE_HEIGHT, playerY));
    }
    
    // AI paddle movement (simple follow)
    if (!isMultiplayer) {
        if (aiY + PADDLE_HEIGHT / 2 < ballY) aiY += PADDLE_SPEED * 0.85;
        else if (aiY + PADDLE_HEIGHT / 2 > ballY) aiY -= PADDLE_SPEED * 0.85;
        aiY = Math.max(0, Math.min(CANVAS_HEIGHT - PADDLE_HEIGHT, aiY));
    }
    // Ball movement
    if (!isMultiplayer) {
        ballX += ballVX;
        ballY += ballVY;
        // Collisions with top/bottom
        if (ballY <= 0 || ballY + BALL_SIZE >= CANVAS_HEIGHT) ballVY *= -1;
        // Collisions with paddles
        if (
            ballX <= 32 && ballY + BALL_SIZE > playerY && ballY < playerY + PADDLE_HEIGHT
        ) {
            ballVX *= -1;
            ballX = 32;
        }
        if (
            ballX + BALL_SIZE >= CANVAS_WIDTH - 32 && ballY + BALL_SIZE > aiY && ballY < aiY + PADDLE_HEIGHT
        ) {
            ballVX *= -1;
            ballX = CANVAS_WIDTH - 32 - BALL_SIZE;
        }
        // Score
        if (ballX < 0) {
            aiScore++;
            if (aiScore >= 5) gameOver = true;
            resetBall();
        }
        if (ballX > CANVAS_WIDTH) {
            playerScore++;
            if (playerScore >= 5) gameOver = true;
            resetBall();
        }
    }
}

function gameLoop(ctx) {
    update();
    draw(ctx);
    if (!isMultiplayer && isGameActive) {
        requestAnimationFrame(() => gameLoop(ctx));
    }
}

function handleKeyDown(e) {
    if (e.key === 'ArrowUp' || e.key === 'w') upPressed = true;
    if (e.key === 'ArrowDown' || e.key === 's') downPressed = true;
    if (e.key === 'r' && gameOver) {
        playerScore = 0; aiScore = 0; gameOver = false; resetBall();
    }
    console.log("Key down:", e.key, "playerY:", playerY);
}
function handleKeyUp(e) {
    if (e.key === 'ArrowUp' || e.key === 'w') upPressed = false;
    if (e.key === 'ArrowDown' || e.key === 's') downPressed = false;
    console.log("Key up:", e.key, "playerY:", playerY);
}

export function enableMultiplayer(side, sendUpdateFn) {
    isMultiplayer = true;
    multiplayerSide = side;
    sendPaddleUpdate = sendUpdateFn;
    // Reset state for multiplayer
    localPlayerY = (CANVAS_HEIGHT - PADDLE_HEIGHT) / 2;
    aiY = (CANVAS_HEIGHT - PADDLE_HEIGHT) / 2;
    ballX = (CANVAS_WIDTH - BALL_SIZE) / 2;
    ballY = (CANVAS_HEIGHT - BALL_SIZE) / 2;
    playerScore = 0;
    aiScore = 0;
    gameOver = false;

    if (isMultiplayer) {
        if (window.multiplayerInterval) clearInterval(window.multiplayerInterval);
        window.multiplayerInterval = setInterval(() => {
            update();
        }, 1000 / 60); // 60 FPS
    }
}

export function renderMultiplayerState(state) {
    console.log("Received game state from server:", JSON.stringify(state));
    console.log("Game state keys:", Object.keys(state));
    if (state) {
        for (const key in state) {
            if (Object.prototype.hasOwnProperty.call(state, key)) {
                console.log(`Key: ${key}, Value:`, state[key]);
            }
        }
    }

    // Defensive: check for required properties (new structure)
    if (!state || typeof state !== 'object') {
        console.error("Invalid game state:", state);
        return;
    }
    if (
        !state.leftPaddle || typeof state.leftPaddle.y !== 'number' ||
        !state.rightPaddle || typeof state.rightPaddle.y !== 'number' ||
        !state.ball || typeof state.ball.x !== 'number' || typeof state.ball.y !== 'number' ||
        typeof state.leftScore !== 'number' || typeof state.rightScore !== 'number'
    ) {
        console.error("Game state missing properties:", state);
        return;
    }

    if (multiplayerSide === 1) {
        playerY = state.leftPaddle.y;
        aiY = state.rightPaddle.y;
        playerScore = state.leftScore;
        aiScore = state.rightScore;
    } else {
        playerY = state.rightPaddle.y;
        aiY = state.leftPaddle.y;
        playerScore = state.rightScore;
        aiScore = state.leftScore;
    }
    ballX = state.ball.x;
    ballY = state.ball.y;
    gameOver = state.gameOver;
    draw(currentCtx);
}

export function startLocalGame() {
    // Reset game state
    isMultiplayer = false;
    isGameActive = true;
    playerScore = 0;
    aiScore = 0;
    gameOver = false;
    playerY = (CANVAS_HEIGHT - PADDLE_HEIGHT) / 2;
    aiY = (CANVAS_HEIGHT - PADDLE_HEIGHT) / 2;
    resetBall();
    
    // Start game loop
    if (currentCtx) {
        gameLoop(currentCtx);
    }
}

let currentCtx = null;

export function initGame() {
    const canvas = document.getElementById('pong-canvas');
    const ctx = canvas.getContext('2d');
    currentCtx = ctx;
    resetBall();
    document.addEventListener('keydown', handleKeyDown);
    document.addEventListener('keyup', handleKeyUp);
    
    // Initial draw to show game board but don't start game loop
    draw(ctx);
}