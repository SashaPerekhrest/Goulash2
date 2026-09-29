# Production-деплой рядом с Goulash1

При push в `main` workflow `.github/workflows/deploy.yml` запускает frontend и backend тесты, публикует два образа в GHCR с тегом commit SHA, затем копирует `compose.prod.yaml` и `scripts/deploy.sh` на сервер и запускает нужный тег. Workflow можно запустить вручную через `Actions -> Deploy -> Run workflow` после первой настройки.

Production Compose имеет постоянное имя проекта `goulash2`. Его база и том `goulash2_postgres_data` независимы от Goulash1. Наружу открывается только frontend на `127.0.0.1:3001` (порт можно поменять в `.env`). API и PostgreSQL доступны лишь внутри сети Compose. Профиль Caddy из локального `compose.yaml` на общем сервере не запускается: порты 80/443 уже обслуживает хостовый Nginx.

## 1. Подготовьте сервер

Далее `DEPLOY_USER` означает пользователя с SSH-доступом и правом работать с Docker. Docker Engine и Compose plugin уже должны быть установлены, как для Goulash1. Убедитесь, что локальный порт 3001 свободен: `sudo ss -ltnp | grep ':3001 '` не должен показать слушающий процесс. При занятом порте выберите другой и используйте его и в `.env`, и в конфиге Nginx.

```sh
sudo install -d -o DEPLOY_USER -g DEPLOY_USER -m 750 /opt/goulash2
```

Создайте `/opt/goulash2/.env` по образцу `.env.production.example` из репозитория (скопируйте его содержимое вручную или загрузите файл). Минимальный пример:

```env
GHCR_OWNER=sashaperekhrest
IMAGE_TAG=latest
FRONTEND_PORT=3001
ASPNETCORE_ENVIRONMENT=Production
POSTGRES_DB=goulash2
POSTGRES_USER=goulash2
POSTGRES_PASSWORD=replace-with-long-random-password
ADMIN_INITIAL_PASSWORD=replace-with-at-least-12-random-characters
AI_ENCRYPTION_KEY=replace-with-output-of-openssl-rand-base64-32
```

Сгенерируйте отдельные случайные значения на сервере, например `openssl rand -hex 32` для пароля БД, `openssl rand -hex 24` для пароля администратора и `openssl rand -base64 32` для мастер-ключа. `AI_ENCRYPTION_KEY` должен декодироваться ровно в 32 байта. Храните резервную копию мастер-ключа вне сервера: замена без переноса уже зашифрованного ключа AI-провайдера сделает его нечитаемым. Значение `IMAGE_TAG` в `.env` нужно для ручных команд; workflow при деплое переопределяет его конкретным SHA.

`ASPNETCORE_ENVIRONMENT` оставляйте `Production` для HTTPS. Для временной диагностики по HTTP можно установить `Development`: в этом режиме приложение принимает HTTP и помечает auth/CSRF cookies как `Secure` только при HTTPS-запросе. По HTTP cookies идут без шифрования; не вводите пароль и не используйте AI-настройки через публичный интернет в этом режиме. Как только HTTPS доступен, верните `Production` и пересоздайте API.

```sh
chmod 600 /opt/goulash2/.env
```

Если пакеты GHCR приватные, можно использовать тот же personal access token (classic), что в Goulash1 хранится как `GHCR_PAT`, при условии что его владелец имеет право читать оба пакета `goulash2-api` и `goulash2-frontend`. Скопируйте значение в repository secret `GHCR_PAT` репозитория Goulash2: secrets разных репозиториев автоматически не передаются. Альтернатива с более узкими правами — отдельный `GHCR_READ_TOKEN` с `read:packages`; при наличии обоих workflow использует `GHCR_READ_TOKEN`. Workflow перед каждым деплоем передаёт выбранный токен по SSH в `docker login --password-stdin` от имени `DEPLOY_USER`; токен не попадает в аргументы команды на сервере. Если токен создан не под owner репозитория, задайте также secret `GHCR_USERNAME` с логином владельца токена. Для публичных GHCR-пакетов эти secrets не нужны.

Для ручного pull или отката можно войти в GHCR под тем же `DEPLOY_USER` на сервере. Не вводите токен в аргументе `docker login`:

```sh
read -rsp 'GHCR read token: ' GHCR_READ_TOKEN; echo
printf '%s' "$GHCR_READ_TOKEN" | docker login ghcr.io -u YOUR_GITHUB_USER --password-stdin
unset GHCR_READ_TOKEN
```

Проверьте, что пользователь сможет выполнять `docker compose` без `sudo`. Учётные данные `docker login` хранятся в его Docker-конфигурации; если вход выполняли под другим пользователем, автоматический pull их не увидит. Goulash1 на том же сервере тоже обновляет Docker-логин, поэтому Goulash2 выполняет свой `docker login` при каждом деплое. Для публичных GHCR-пакетов вход не требуется.

## 2. Настройте SSH и GitHub Actions

Если у deploy-пользователя уже есть подходящий ключ Goulash1, можно использовать его. Для отдельного ключа создайте пару на доверенной машине командой `ssh-keygen -t ed25519 -f ./goulash2_deploy_key`, добавьте содержимое `.pub` в `~DEPLOY_USER/.ssh/authorized_keys` на сервере и проверьте вход. Приватный ключ целиком добавьте в GitHub Secret `SERVER_SSH_KEY` репозитория Goulash2.

В `Settings -> Secrets and variables -> Actions -> Repository secrets` Goulash2 добавьте:

| Secret | Значение |
| --- | --- |
| `SERVER_HOST` | IP или DNS-имя сервера без протокола |
| `SERVER_PORT` | SSH-порт; можно не задавать при порте 22 |
| `SERVER_USER` | `DEPLOY_USER` |
| `SERVER_SSH_KEY` | приватный SSH-ключ с исходными переносами строк |
| `DEPLOY_PATH` | `/opt/goulash2` |
| `GHCR_PAT` или `GHCR_READ_TOKEN` | токен classic с правом чтения пакетов, если они приватные; при наличии обоих используется `GHCR_READ_TOKEN` |
| `GHCR_USERNAME` | логин владельца токена, если он отличается от owner репозитория |

Workflow настраивает SSH так же, как Goulash1: получает host key командой `ssh-keyscan` во время запуска и сохраняет его в `known_hosts` для этого job. Поэтому секрет `SERVER_SSH_KNOWN_HOSTS` не нужен. `SERVER_PORT` задавать не нужно при стандартном порте 22.

Workflow публикует образы от имени встроенного `GITHUB_TOKEN` и не требует PAT в GitHub Secrets. Если namespace GHCR должен отличаться от owner репозитория, задайте repository variable `GHCR_OWNER` (строчными буквами) и убедитесь, что `GITHUB_TOKEN` имеет право публиковать в этот namespace. В обычном случае `GHCR_OWNER` не нужен. Для данного репозитория ожидается `sashaperekhrest`.

## 3. Подключите поддомен к хостовому Nginx

Создайте A/AAAA-запись нового поддомена, указывающую на этот сервер. Подставьте реальное имя вместо `goulash2.example.com` и проверьте выбранный `FRONTEND_PORT`. Создайте отдельный файл `/etc/nginx/sites-available/goulash2.example.com`:

```nginx
server {
    listen 80;
    server_name goulash2.example.com;

    location / {
        proxy_pass http://127.0.0.1:3001;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

Внутренний Nginx frontend обслуживает SPA и передаёт `/api/` и `/openapi/` контейнеру API, сохраняя `X-Forwarded-Proto`. Поэтому отдельные маршруты API в хостовом Nginx не нужны. Включите сайт и проверьте конфигурацию:

```sh
sudo ln -s /etc/nginx/sites-available/goulash2.example.com /etc/nginx/sites-enabled/goulash2.example.com
sudo nginx -t
sudo systemctl reload nginx
```

Когда DNS указывает на сервер, выпустите сертификат тем же установленным Certbot, что используется для Goulash1: `sudo certbot --nginx -d goulash2.example.com`. Проверьте `sudo certbot renew --dry-run`. В Production приложение выдаёт cookie с `Secure`, поэтому пользовательский доступ должен идти через HTTPS.

## 4. Первый деплой и проверка

После подготовки сервера запустите workflow вручную или выполните push в `main`. В Actions должны пройти тесты, обе публикации GHCR и job `Deploy to server`. На сервере:

```sh
cd /opt/goulash2
docker compose -f compose.prod.yaml ps
docker compose -f compose.prod.yaml logs --tail=100 api
curl -fsS http://127.0.0.1:3001/api/v1/health/ready
curl -fsS https://goulash2.example.com/api/v1/health/ready
```

Оба запроса готовности должны вернуть `{"status":"ready"}`. Откройте сайт по HTTPS, войдите паролем администратора, сохраните ключ AI-провайдера в настройках и проверьте реальный поиск, каталог и сохранение избранного/заметки. API применяет миграции при старте одного экземпляра.

## Обновления, резервные копии и откат

Перед выпуском с миграциями сохраните дамп под `DEPLOY_USER` в каталог с ограниченным доступом:

```sh
cd /opt/goulash2
umask 077
docker compose -f compose.prod.yaml exec -T db sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' > goulash2-$(date +%F-%H%M).dump
```

Скопируйте дамп за пределы сервера и периодически проверяйте восстановление на отдельной БД. Для отката образов возьмите SHA предыдущего успешного workflow и выполните в `/opt/goulash2`:

```sh
IMAGE_TAG=PREVIOUS_COMMIT_SHA sh scripts/deploy.sh
```

Это откатывает образы, но не миграцию данных. При несовместимой миграции восстановите базу из проверенного дампа по отдельному плану восстановления. Не используйте `docker compose down -v` или `docker volume prune` для обычного обновления. Скрипт Goulash2 не очищает общие Docker-образы и cache сервера. Скрипт Goulash1 сейчас удаляет все неиспользуемые образы на хосте; если он убрал старый образ Goulash2, команда отката скачает его заново из GHCR.
