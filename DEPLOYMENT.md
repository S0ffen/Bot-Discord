# Wysyłanie paczki bota na VPS

Aktualny skrypt wykonuje wyłącznie przygotowanie i wysłanie pliku `.zip`.

Konfiguracja:

- VPS: `51.75.73.137`,
- użytkownik SSH: `ubuntu`,
- katalog lokalnych paczek: `versions`,
- katalog docelowy na VPS-ie: `/home/ubuntu/`.

## Uruchomienie

Na Windowsie uruchom dwuklikiem:

```bat
deploy\Deploy.bat
```

Skrypt:

1. poprosi o wersję, na przykład `1.0.0`,
2. uruchomi testy Release,
3. utworzy `versions/discord-activity-bot-1.0.0.zip`,
4. sprawdzi, czy paczka nie zawiera tokenu, lokalnej bazy, `.env`, `bin` ani `obj`,
5. wyśle ją przez SCP jako `/home/ubuntu/discord-activity-bot-1.0.0.zip`.

Jeśli SSH nie korzysta z klucza, pojawi się prośba o hasło użytkownika `ubuntu`.

Skrypt nie używa `sudo`, nie tworzy katalogów w `/opt`, nie rozpakowuje paczki, nie wykonuje poleceń Dockera i nie uruchamia bota.

## Dalszy etap

Rozpakowanie paczki, przygotowanie trwałej bazy SQLite, backup i uruchomienie Dockera na VPS-ie zostaną opisane oraz dodane później jako osobny etap serwerowy.
