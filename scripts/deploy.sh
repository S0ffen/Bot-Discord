#!/usr/bin/env bash
set -Eeuo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"

volume_name="discord-activity-bot-data"
container_name="discord-activity-bot"
backup_dir="$project_dir/backups"

if [[ ! -f .env ]]; then
    echo "Brak pliku .env. Skopiuj .env.example do .env i uzupełnij token."
    exit 1
fi

if ! docker volume inspect "$volume_name" >/dev/null 2>&1; then
    docker volume create "$volume_name" >/dev/null
    echo "Utworzono trwały wolumen $volume_name."
fi

mkdir -p "$backup_dir"

echo "Budowanie obrazu i uruchamianie testów..."
docker compose build --pull bot
docker pull alpine:3 >/dev/null

had_container=false
if docker container inspect "$container_name" >/dev/null 2>&1; then
    had_container=true
    echo "Zatrzymywanie obecnej wersji bota..."
    docker compose stop -t 30 bot
fi

restore_previous_on_error() {
    if [[ "$had_container" == true ]]; then
        echo "Aktualizacja nie powiodła się. Ponownie uruchamiam poprzedni kontener."
        docker compose up -d --no-build bot || true
    fi
}
trap restore_previous_on_error ERR

timestamp="$(date -u +'%Y%m%d-%H%M%S')"
backup_name="activity-bot-$timestamp.tar.gz"
echo "Tworzenie backupu: backups/$backup_name"
docker run --rm \
    --volume "$volume_name:/data:ro" \
    --volume "$backup_dir:/backups" \
    alpine:3 \
    tar -czf "/backups/$backup_name" -C /data .

echo "Uruchamianie nowej wersji..."
docker compose up -d --no-build bot
trap - ERR

echo "Gotowe. Ostatnie logi:"
docker compose logs --tail=30 bot
