# GitHub Release Template

## Tag
`v3.0.0`

## Release Title
`CHRONOS v3.0.0 — .ZIP Export for osu!lazer, Built-in DPI Bypass & OAuth`

---

## Release Notes (Русский)

Первый официальный релиз CHRONOS — высокоскоростного загрузчика и менеджера коллекций с osu!collector для osu!lazer и osu!stable с полным комплексом решений для стабильной работы без VPN и сторонних прокси.

### Ключевые изменения

#### 1. Встроенный сетевой обход блокировок ТСПУ (DPI Bypass)
* Реализована низкоуровневая сегментация пакетов `TCP SNI Fragmentation`: TLS ClientHello разбивается на несколько TCP-сегментов прямо перед передачей Server Name Indication.
* Программа работает автономно в любых регионах с блокировками (включая РФ) без необходимости запускать zapret, GoodbyeDPI или VPN.
* Встроен отказоустойчивый резолвер Cloudflare IP для защиты от DNS-спуфинга и подмены адресов на узлах фильтрации.

#### 2. Авторизация через официальный osu! OAuth 2.0
* Добавлена авторизация в аккаунте osu! через браузер в один клик (локальный обработчик `http://localhost:7270/callback`).
* При авторизованном профиле карты скачиваются напрямую с официальных CDN-серверов `osu.ppy.sh` без ограничений зеркал.
* Токены сессии безопасно сохраняются в `%APPDATA%\CHRONOS\chronos.json` и автоматически обновляются через `refresh_token`.

#### 3. Интеллектуальная сеть зеркал и докачка (HTTP Range)
* Многоуровневый каскад загрузки: `osu.ppy.sh` -> `beatconnect.io` -> `catboy.best` -> `osu.direct` -> `nerinyan.moe` -> `sayobot`.
* Оптимизированы сетевые таймауты (переключение на следующее зеркало за 8 секунд при недоступности или троттлинге).
* Автоматическая докачка поврежденных или прерванных файлов с помощью `HTTP Range: bytes=...`.

#### 4. Поддержка osu!lazer и osu!stable
* osu!lazer: мгновенный импорт через локальный IPC и прямая синхронизация коллекций через базу данных `client.realm`.
* **Выбор режима загрузки для lazer (.zip / в игру)**: интерактивный выбор с предупреждением и рекомендацией сохранять карты в `.zip` архив для гарантированного обхода лимита lazer на установку более 350 карт в день.
* osu!stable: загрузка карт в каталог `Songs` и автоматическое обновление структуры `collection.db`.
* Проверка дубликатов: скачиваются только отсутствующие в игре карты.

#### 5. Автономность (Self-Contained)
* Программа собрана в один исполняемый файл `CHRONOS.exe` (Windows x64).
* Не требует предварительной установки .NET SDK или .NET Runtime.

---

### Файлы релиза
* `CHRONOS.exe` — готовый автономный бинарник для Windows x64.

---

## Release Notes (English)

Initial release of CHRONOS — a high-speed beatmap collection downloader and manager for osu!lazer and osu!stable, designed for uninterrupted operation without requiring VPN or third-party proxies.

### Features & Improvements

* **Built-in TCP SNI DPI Bypass**:
  * Integrated low-level TCP packet fragmentation for TLS ClientHello to bypass ISP DPI filtering.
  * Direct connectivity to `osucollector.com` and beatservices without external tools.
  * Anti-censorship Cloudflare IP resolution fallback.

* **Official osu! OAuth 2.0 Integration**:
  * Browser-based authorization with a single click (`http://localhost:7270/callback`).
  * Direct downloads from official `osu.ppy.sh` CDN at native speed.
  * Persistent token storage with automatic renewal via `refresh_token`.

* **Fallback Mirror Architecture & HTTP Range Resume**:
  * Multi-tier fallback hierarchy: `osu.ppy.sh` -> `beatconnect.io` -> `catboy.best` -> `osu.direct` -> `nerinyan.moe` -> `sayobot`.
  * Fast failover (8-second timeout per mirror on stalls or rate limits).
  * Partial content resume using `HTTP Range` headers.

* **Dual Platform Synchronization**:
  * Full support for osu!lazer (IPC / Realm DB) and osu!stable (`Songs` folder / `collection.db`).
  * Smart deduplication against existing beatmaps.

* **Single-File Distribution**:
  * Standalone Windows x64 executable. No external runtimes or dependencies needed.
