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
let aiY = (CANVAS_HEIGHT - PADDLE_HEIGHT) / 2;
let ballX = (CANVAS_WIDTH - BALL_SIZE) / 2;
let ballY = (CANVAS_HEIGHT - BALL_SIZE) / 2;
let ballVX = BALL_SPEED, ballVY = BALL_SPEED;
let playerScore = 0, aiScore = 0;
let upPressed = false, downPressed = false;
let gameOver = false;

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
    // Draw paddles
    ctx.fillStyle = '#fff';
    ctx.fillRect(16, playerY, PADDLE_WIDTH, PADDLE_HEIGHT);
    ctx.fillRect(CANVAS_WIDTH - 32, aiY, PADDLE_WIDTH, PADDLE_HEIGHT);
    // Draw ball
    ctx.fillRect(ballX, ballY, BALL_SIZE, BALL_SIZE);
    // Draw scores
    ctx.font = '48px monospace';
    ctx.textAlign = 'center';
    ctx.fillText(playerScore, CANVAS_WIDTH / 2 - 50, 60);
    ctx.fillText(aiScore, CANVAS_WIDTH / 2 + 50, 60);
    // Game over
    if (gameOver) {
        ctx.font = '36px monospace';
        ctx.fillText('Game Over! Press R to Restart', CANVAS_WIDTH / 2, CANVAS_HEIGHT / 2);
    }
}

function update() {
    if (gameOver) return;
    // Player paddle movement
    if (upPressed) playerY -= PADDLE_SPEED;
    if (downPressed) playerY += PADDLE_SPEED;
    playerY = Math.max(0, Math.min(CANVAS_HEIGHT - PADDLE_HEIGHT, playerY));
    // AI paddle movement (simple follow)
    if (aiY + PADDLE_HEIGHT / 2 < ballY) aiY += PADDLE_SPEED * 0.85;
    else if (aiY + PADDLE_HEIGHT / 2 > ballY) aiY -= PADDLE_SPEED * 0.85;
    aiY = Math.max(0, Math.min(CANVAS_HEIGHT - PADDLE_HEIGHT, aiY));
    // Ball movement
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

function gameLoop(ctx) {
    update();
    draw(ctx);
    requestAnimationFrame(() => gameLoop(ctx));
}

function handleKeyDown(e) {
    if (e.key === 'ArrowUp' || e.key === 'w') upPressed = true;
    if (e.key === 'ArrowDown' || e.key === 's') downPressed = true;
    if (e.key === 'r' && gameOver) {
        playerScore = 0; aiScore = 0; gameOver = false; resetBall();
    }
}
function handleKeyUp(e) {
    if (e.key === 'ArrowUp' || e.key === 'w') upPressed = false;
    if (e.key === 'ArrowDown' || e.key === 's') downPressed = false;
}

export function initGame() {
    const canvas = document.getElementById('pong-canvas');
    const ctx = canvas.getContext('2d');
    resetBall();
    document.addEventListener('keydown', handleKeyDown);
    document.addEventListener('keyup', handleKeyUp);
    gameLoop(ctx);
}