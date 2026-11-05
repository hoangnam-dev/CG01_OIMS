$ErrorActionPreference = 'Stop'

$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$bytes = New-Object byte[] 32
$rng.GetBytes($bytes)
$env:OIMS_POSTGRES_PASSWORD = [Convert]::ToBase64String($bytes)
$rng.GetBytes($bytes)
$env:OIMS_REDIS_PASSWORD = [Convert]::ToBase64String($bytes)
$rng.GetBytes($bytes)
$env:OIMS_RABBITMQ_PASSWORD = [Convert]::ToBase64String($bytes)
$rng.GetBytes($bytes)
$env:OIMS_JWT_SIGNING_KEY = [Convert]::ToBase64String($bytes)
$rng.Dispose()

$env:OIMS_POSTGRES_PORT = '0'
$env:OIMS_REDIS_PORT = '0'
$env:OIMS_RABBITMQ_PORT = '0'
$env:OIMS_RABBITMQ_MANAGEMENT_PORT = '0'
$env:OIMS_API_PORT = '0'

$project = 'oims-e2e-' + [guid]::NewGuid().ToString('N').Substring(0, 8)

try
{
    docker compose --project-name $project config --quiet
    if ($LASTEXITCODE -ne 0) { throw 'E2E Compose configuration failed' }

    docker compose --project-name $project up --build --detach --wait --wait-timeout 180 --quiet-build
    if ($LASTEXITCODE -ne 0) { throw 'E2E Compose cold start failed' }

    $services = @(docker compose --project-name $project ps --all --format json | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'E2E service inspection failed' }

    $migration = $services | Where-Object Service -eq 'migrate'
    $api = $services | Where-Object Service -eq 'api'
    $worker = $services | Where-Object Service -eq 'worker'
    if ($migration.State -ne 'exited' -or $migration.ExitCode -ne 0) { throw 'E2E migration job did not finish successfully' }
    if ($api.State -ne 'running' -or $api.Health -ne 'healthy') { throw 'E2E API is not ready' }
    if ($worker.State -ne 'running') { throw 'E2E Worker is not running' }

    $migrationCount = 'SELECT COUNT(*) FROM "__EFMigrationsHistory";' | docker compose --project-name $project exec -T postgres psql -U oims -d oims -tA
    if ($LASTEXITCODE -ne 0 -or [int]$migrationCount -le 0) { throw 'E2E database has no applied migrations' }

    $productResponse = docker compose --project-name $project exec -T api wget -q -O - http://127.0.0.1:8080/api/products/
    if ($LASTEXITCODE -ne 0) { throw 'E2E Product read failed' }
    $products = $productResponse | ConvertFrom-Json
    if ($null -eq $products.data) { throw 'E2E Product response has no data field' }

    docker compose --project-name $project stop redis
    if ($LASTEXITCODE -ne 0) { throw 'E2E Redis stop failed' }

    $productResponse = docker compose --project-name $project exec -T api wget -q -O - http://127.0.0.1:8080/api/products/
    if ($LASTEXITCODE -ne 0) { throw 'E2E Product read failed during Redis outage' }
    $products = $productResponse | ConvertFrom-Json
    if ($null -eq $products.data) { throw 'E2E Product fallback response has no data field' }

    docker compose --project-name $project start redis
    if ($LASTEXITCODE -ne 0) { throw 'E2E Redis restart failed' }
    docker compose --project-name $project up --detach --wait --wait-timeout 60 redis
    if ($LASTEXITCODE -ne 0) { throw 'E2E Redis recovery failed' }

    Write-Output "E2E Compose smoke passed for $project with $migrationCount applied migrations"
}
finally
{
    docker compose --project-name $project down --volumes --remove-orphans | Out-Null
}
