# Wdrożenie bota na VPS z zachowaniem SQLite

Bot nie potrzebuje otwartych portów przychodzących. Łączy się z Discordem na zewnątrz przez HTTPS/WebSocket.

## Dlaczego dane nie znikną

Plik SQLite znajduje się w `/app/data/activity-bot.db`, a `/app/data` jest montowane z zewnętrznego wolumenu Dockera `discord-activity-bot-data`. Obraz oraz kontener można przebudowywać i usuwać bez naruszania tego wolumenu.

Skrypt `scripts/deploy.sh` przed podmianą działającego kontenera zatrzymuje bota i zapisuje kopię całego wolumenu w lokalnym katalogu `backups/`. Dzięki zatrzymaniu procesu kopia SQLite jest spójna również wtedy, gdy baza używa plików WAL.

Nie usuwaj ręcznie wolumenu `discord-activity-bot-data`. Skrypt wdrożeniowy nigdy tego nie robi.

## Pierwsze wdrożenie

1. Zainstaluj na VPS-ie Docker Engine z wtyczką Docker Compose.
2. Umieść projekt na VPS-ie, na przykład w `/opt/discord-activity-bot`, i przejdź do tego katalogu.
3. Przygotuj sekrety:

   ```bash
   cp .env.example .env
   nano .env
   chmod 600 .env
   ```

4. W `.env` wpisz prawidłowy `DISCORD_TOKEN` i `Bot__TestGuildId`.
5. Uruchom wdrożenie:

   ```bash
   bash scripts/deploy.sh
   ```

6. Obserwuj logi:

   ```bash
   docker compose logs -f --tail=100 bot
   ```

## Opcjonalnie: przeniesienie obecnej bazy z Windowsa

Jeśli dane testowe mają przejść na VPS, najpierw zatrzymaj lokalnego bota przez `Ctrl+C`. W PowerShellu, w katalogu projektu, spakuj cały katalog danych (razem z ewentualnymi plikami `-wal` i `-shm`):

```powershell
tar -czf initial-data.tar.gz -C src/DiscordActivityBot/bin/Debug/net8.0/data .
```

Prześlij `initial-data.tar.gz` do głównego katalogu projektu na VPS-ie. Przed pierwszym `deploy.sh` wykonaj tam:

```bash
docker volume create discord-activity-bot-data
docker run --rm \
  --volume discord-activity-bot-data:/data \
  --volume "$PWD:/import:ro" \
  alpine:3 \
  tar -xzf /import/initial-data.tar.gz -C /data
```

Następnie uruchom `bash scripts/deploy.sh`. Archiwum zawiera dane użytkowników, więc po udanym imporcie przechowuj je tak samo ostrożnie jak backupy.

## Każda następna aktualizacja

Wgraj albo pobierz nową wersję kodu, nie nadpisując `.env`, a następnie wykonaj:

```bash
bash scripts/deploy.sh
```

Skrypt kolejno:

1. buduje obraz i uruchamia testy,
2. zatrzymuje starą wersję,
3. zapisuje backup SQLite w `backups/`,
4. uruchamia nowy kontener z tym samym wolumenem danych.

Zmiany schematu bazy muszą być wykonywane przez kod inicjalizujący lub migracje, nigdy przez skasowanie pliku SQLite. Obecny bot wykonuje takie aktualizacje bez kasowania punktów, historii zakupów i sesji voice.

## Przydatne polecenia

```bash
# status
docker compose ps

# ostatnie logi
docker compose logs --tail=100 bot

# restart bez przebudowy
docker compose restart bot

# zatrzymanie i ponowne uruchomienie
docker compose stop -t 30 bot
docker compose up -d bot

# lista backupów
ls -lh backups/

# potwierdzenie istnienia trwałego wolumenu
docker volume inspect discord-activity-bot-data
```

Nie przechowuj tokenu w `appsettings.json`, obrazie Dockera ani repozytorium. Plik `.env`, katalog `backups/` i lokalne bazy są ignorowane przez Git i kontekst budowania obrazu.
