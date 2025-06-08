#!/usr/bin/env pwsh

Write-Host "Starting test for memory cache implementation" -ForegroundColor Green

# Start Redis and SignalR Emulator for other dependencies
Write-Host "Starting Redis..." -ForegroundColor Yellow
wsl sudo service redis-server start

Write-Host "Starting SignalR Emulator..." -ForegroundColor Yellow
asrs-emulator upstream init
asrs-emulator start

# Start the backend
Write-Host "Starting backend API..." -ForegroundColor Yellow
cd $PSScriptRoot\..\backend
Start-Process -FilePath "dotnet" -ArgumentList "run" -NoNewWindow

# Give the server time to start
Start-Sleep -Seconds 5

Write-Host "Starting frontend server..." -ForegroundColor Yellow
cd $PSScriptRoot\..\frontend\src
Start-Process -FilePath "npx" -ArgumentList "http-server . -p 8080" -NoNewWindow

# Open the application in the browser
Start-Process "http://localhost:8080"

Write-Host "Test environment is ready!" -ForegroundColor Green
Write-Host "The game is now using memory cache for paddle positions instead of Redis" -ForegroundColor Green
Write-Host "Press Ctrl+C to stop the servers" -ForegroundColor Yellow

# Keep the script running
try {
    while ($true) {
        Start-Sleep -Seconds 1
    }
} finally {
    # Cleanup
    Stop-Process -Name "dotnet" -ErrorAction SilentlyContinue
    Stop-Process -Name "node" -ErrorAction SilentlyContinue
}
