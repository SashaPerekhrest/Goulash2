# API-контракты

## 1. Общие правила

- Базовый путь: `/api/v1`. Запросы и ответы — JSON UTF-8; даты — ISO 8601 в UTC; идентификаторы — UUID.
- Для всех маршрутов, кроме проверки готовности и получения CSRF-токена/входа, требуется cookie-сессия администратора. Для `POST`, `PUT`, `PATCH`, `DELETE` нужен заголовок `X-CSRF-Token`, в том числе для входа. CSRF-cookie HttpOnly и SameSite=Lax; session-cookie также HttpOnly и SameSite=Lax, действует 8 часов без продления. В опубликованной среде обе cookie имеют Secure.
- Успешный `GET` возвращает `200`, создание/поиск — `200`, изменение — `200` или `204` как указано ниже. Неизвестный ID — `404`.
- Отсутствующее внешнее значение представлено как `value: null`, `status: "missing"`, `sources: []`. Нельзя заменять его строкой «Нет данных» в API.
- Числовые значения передаются числами, деньги — десятичными строками во избежание ошибок округления. Валюта — трёхбуквенный код, единица измерения — явная строка (`kg`, `piece`, `box` и т. п.).
- Максимум длины свободного запроса — 500 символов; заметки — 2000; `pageSize` — 10–50.

### 1.1. Общий формат ошибки

Используется `application/problem+json`:

```json
{
  "type": "https://example.local/problems/validation-error",
  "title": "Некорректные параметры поиска",
  "status": 400,
  "code": "VALIDATION_ERROR",
  "detail": "price.max должен быть не меньше price.min",
  "traceId": "00-..."
}
```

Коды: `VALIDATION_ERROR`/`UNSUPPORTED_PROVIDER`/`UNSUPPORTED_MODEL` (`400`), `UNAUTHORIZED`/`INVALID_CREDENTIALS` (`401`), `FORBIDDEN`/`CSRF_INVALID` (`403`), `NOT_FOUND` (`404`), `CONFLICT`/`PROVIDER_NOT_CONFIGURED`/`API_KEY_REQUIRED` (`409`), `PROVIDER_UNAVAILABLE`/`PROVIDER_INVALID_RESPONSE` (`502`), `PROVIDER_TIMEOUT` (`504`), `RATE_LIMITED` (`429`). Поля с секретами и внутренние тексты промптов в ошибках не возвращаются. Вход ограничен пятью попытками за 15 минут на IP.

## 2. Авторизация и состояние

| Метод и путь | Назначение | Ответ |
| --- | --- | --- |
| `GET /auth/csrf` | Выдать CSRF cookie и токен до входа | `200 {"csrfToken":"..."}` |
| `POST /auth/login` | Вход по паролю администратора: `{"password":"..."}` | `204`, защищённая session cookie |
| `POST /auth/logout` | Завершить сессию | `204` |
| `GET /auth/session` | Текущий статус входа | `200 {"authenticated":true}` либо `401` |
| `GET /health/ready` | Готовность backend и PostgreSQL | `200` либо `503` |

`GET /auth/csrf` устанавливает HttpOnly CSRF-cookie и возвращает request token; клиент передаёт его в заголовке изменяющих запросов, включая вход. Пароль администратора задаётся секретом окружения `ADMIN_INITIAL_PASSWORD` (конфигурация `Admin:InitialPassword`, не менее 12 символов). Выход отзывает выданную сессию на сервере; перезапуск API завершает все сессии. При `401` frontend переводит пользователя на страницу входа.

## 3. Настройки ИИ

### `GET /ai/providers`

Возвращает только адаптеры, собранные в backend и поддерживающие веб-поиск:

```json
{
  "items": [
    { "id": "perplexity", "displayName": "Perplexity", "supportsWebSearch": true,
      "models": ["sonar", "sonar-pro", "sonar-deep-research", "sonar-reasoning-pro"] }
  ]
}
```

`provider-id` — идентификатор реализованного адаптера, не произвольный URL. `models` содержит точные поддерживаемые идентификаторы; API отклоняет неизвестную модель как `UNSUPPORTED_MODEL`. У Perplexity по умолчанию рекомендуется `sonar-pro`.

Подключён Perplexity Sonar API: `POST https://api.perplexity.ai/v1/sonar`, авторизация Bearer API key. Ответ содержит `choices[0].message.content` с JSON поставщиков и отдельный `search_results[]` с `url`, `title`, `snippet` и датами публикации/обновления. В факты попадают только сниппеты из `search_results`, URL которых совпал с источником из JSON; ответ модели сам по себе источником не является. URL-ы должны быть публичными абсолютными HTTP(S)-адресами, а сниппет должен подтверждать значение.

Адаптер реализует `Goulash.Application.IAiProviderAdapter`: идентификатор, отображаемое имя, признак веб-поиска, поддерживаемые модели и `CheckConnectionAsync(model, apiKey, cancellationToken)`, возвращающий соединение и доступность веб-поиска. В список попадают только зарегистрированные сервером адаптеры с `SupportsWebSearch=true`.

`ISupplierDiscoveryProvider.DiscoverAsync(query, filters, limit, cancellationToken)` загружает модель и ключ из сохранённых настроек. Адаптер передаёт структурированные фильтры, жёсткий максимум пять и запрет домысливания; конкретные реализации провайдера не раскрываются клиенту.

### `GET /ai/settings`

```json
{
  "providerId": "provider-id",
  "model": "model-id",
  "hasApiKey": true,
  "apiKeyMask": "••••1234",
  "updatedAt": "2026-09-26T10:00:00Z"
}
```

Если интеграция ещё не настроена: `providerId` и `model` равны `null`, `hasApiKey` — `false`, `apiKeyMask` — `null`. Полный ключ ни один `GET` не возвращает.

### `PUT /ai/settings`

```json
{
  "providerId": "provider-id",
  "model": "model-id",
  "apiKey": "new-secret"
}
```

Ответ `200` в форме `GET /ai/settings`. `apiKey` можно опустить для сохранения прежнего ключа **только при неизменном `providerId` и наличии ранее сохранённого ключа**. При смене провайдера или после удаления ключа новый ключ обязателен (`409 API_KEY_REQUIRED`). Пустая строка недопустима. Неизвестный или не поддерживающий веб-поиск адаптер отклоняется (`400 UNSUPPORTED_PROVIDER`). Ключ шифруется AES-256-GCM с Base64 32-байтовым секретом окружения `AI_ENCRYPTION_KEY` (`Ai:EncryptionKey`) до записи в базу.

### `DELETE /ai/settings/key`

Удаляет сохранённый ключ, оставляя выбранные провайдер и модель. Ответ `204`; поиск после этого недоступен.

### `POST /ai/settings/check`

Выполняет минимальную проверку текущих настроек и доступа к веб-источникам. Ответ `200`:

```json
{ "connected": true, "webSearchAvailable": true, "checkedAt": "2026-09-26T10:00:00Z" }
```

При ошибке — Problem Details с кодом `PROVIDER_NOT_CONFIGURED`, `PROVIDER_UNAVAILABLE`, `PROVIDER_INVALID_RESPONSE` или `PROVIDER_TIMEOUT`. Ключ в запросе проверки не передаётся. Проверка делает минимальный веб-запрос и возвращает успех только когда ответ содержит минимум один URL и сниппет веб-результата. Ошибка не изменяет сохранённые настройки.

## 4. Поиск новых поставщиков

### `POST /discoveries`

Запускает один синхронный поиск через ИИ-провайдера и сохранение результатов. Хотя бы непустой после обрезки пробелов `query` либо один содержательный фильтр обязателен. Содержательные фильтры: непустые `city`, `region`, `category`, `product`, хотя бы одна граница `price`, `maxDeliveryDays` или хотя бы одна граница минимального заказа. Флаг `includeApproximatePrices`, валюта и единица без ценовой границы сами по себе поиск не запускают.

```json
{
  "query": "поставщики замороженных ягод",
  "filters": {
    "city": "Екатеринбург",
    "region": "Свердловская область",
    "category": "ингредиенты",
    "product": "малина",
    "price": { "min": "100.00", "max": "500.00", "currency": "RUB", "unit": "kg" },
    "includeApproximatePrices": false,
    "maxDeliveryDays": 7,
    "minMinimumOrder": { "amount": "10", "unit": "kg" },
    "maxMinimumOrder": { "amount": "100", "unit": "kg" }
  }
}
```

Все поля `filters` необязательны. Для `price` при наличии хотя бы одной границы обязательны `currency` и `unit`. По умолчанию `includeApproximatePrices` равен `false`, и ценовой фильтр учитывает только точные цены. При `true` он также учитывает примерные цены, которые интерфейс обязан пометить «ориентировочно». Для точной и разрешённой примерной цены с диапазоном совпадением считается пересечение диапазона цены с заданными границами; неизвестные значения не проходят фильтр. Для `minMinimumOrder` и `maxMinimumOrder` в теле запроса указывается `amount` и `unit`; если заданы обе границы, их единицы должны совпадать. В запросе базы единица передаётся общим параметром `minimumOrderUnit`. Известный минимальный заказ должен быть не меньше нижней и не больше верхней границы; нижняя граница не может превышать верхнюю. Неотрицательные границы и сроки проверяются сервером. Несопоставимое числовое значение не проходит соответствующий фильтр. Город и регион сопоставляются с местом работы или подтверждённой зоной доставки, а не только с адресом офиса.

Ответ `200`:

```json
{
  "discoveryId": "f2613141-f43f-4208-9c49-25f98c2ef613",
  "items": [
    {
      "id": "ad2dc9d7-c64d-4d5e-9a60-a954c057a570",
      "name": "Пример поставщика",
      "city": "Екатеринбург",
      "products": ["замороженная малина"],
      "pricePreview": "от 250 RUB/кг",
      "priceIsApproximate": false,
      "deliveryPreview": "до 5 дней",
      "websiteUrl": "https://example.com",
      "contactPreview": "+7 000 000-00-00",
      "isFavorite": false,
      "hasUnconfirmedData": true,
      "lastDiscoveredAt": "2026-09-26T10:00:00Z"
    }
  ],
  "acceptedCount": 1,
  "rejectedCount": 0,
  "updatedExistingCount": 0
}
```

`items` содержит максимум пять элементов и может быть пустым. Ограничение применяется после проверки записей и условий поиска. `rejectedCount` показывает количество записей провайдера, отклонённых валидацией или условиями поиска; подходящие записи сверх лимита не считаются отклонёнными. При ответе провайдера без пригодных записей возвращается пустой список, если сам ответ корректен; при нарушении схемы оболочки ответа — `PROVIDER_INVALID_RESPONSE`. Некорректная отдельная запись пропускается. Сохранение принятых записей и счётчиков выполняется транзакционно.

В карточках поиска и в списке базы `name` — строка для отображения текущего выбранного названия. Полный источник, статус и альтернативные варианты названия возвращаются в `GET /suppliers/{id}` как `SourcedValue<string>`. `hasUnconfirmedData` равен `true`, если хотя бы одно отображаемое внешнее значение, включая название, имеет статус `external`. На карточке он даёт общий знак «Есть неподтверждённые сведения»; статус и источник конкретного поля пользователь видит в деталях. При `priceIsApproximate: true` показанная цена дополнительно помечается «ориентировочно».

## 5. База поставщиков

### `GET /suppliers`

Query-параметры:

| Параметр | Тип | Правило |
| --- | --- | --- |
| `q` | string | Поиск по названию и товарам |
| `city`, `region`, `category`, `product` | string | Фильтры без учёта регистра |
| `priceMin`, `priceMax`, `currency`, `unit` | decimal string/string | Для цены обязательны валюта и единица |
| `includeApproximatePrices` | boolean | По умолчанию `false`; разрешает учитывать примерные цены, которые UI помечает «ориентировочно» |
| `maxDeliveryDays` | integer | Только сопоставимые известные сроки |
| `minMinimumOrder`, `maxMinimumOrder`, `minimumOrderUnit` | decimal string/string | Нижняя и верхняя границы минимального заказа; если задана хотя бы одна, обязательна единица |
| `favoriteOnly` | boolean | По умолчанию `false` |
| `sort` | enum | `created_desc` (по умолчанию), `created_asc`, `name_asc`, `name_desc` |
| `page` | integer | С 1; по умолчанию 1 |
| `pageSize` | integer | 10–50; по умолчанию 20 |

Ответ `200`:

```json
{
  "items": [
    {
      "id": "ad2dc9d7-c64d-4d5e-9a60-a954c057a570",
      "name": "Пример поставщика",
      "city": "Екатеринбург",
      "products": ["замороженная малина"],
      "pricePreview": "от 250 RUB/кг",
      "priceIsApproximate": false,
      "deliveryPreview": "до 5 дней",
      "websiteUrl": "https://example.com",
      "contactPreview": "+7 000 000-00-00",
      "isFavorite": false,
      "hasUnconfirmedData": true,
      "lastDiscoveredAt": "2026-09-26T10:00:00Z"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalItems": 1,
  "totalPages": 1
}
```

Этот маршрут никогда не вызывает ИИ-провайдера. `pricePreview`, `deliveryPreview`, `websiteUrl`, `contactPreview` могут быть `null`. В карточках обоих маршрутов `priceIsApproximate` равен `true` только для показанной примерной цены; при отсутствии цены — `false`.

### `GET /suppliers/{id}`

Полная запись. Каждый внешний атрибут, включая название поставщика, имеет единый тип `SourcedValue<T>`:

```json
{
  "value": "+7 000 000-00-00",
  "status": "external",
  "sources": [
    {
      "url": "https://catalog.example/supplier",
      "title": "Карточка компании",
      "excerpt": "Поставки по Свердловской области в течение пяти дней",
      "type": "external",
      "retrievedAt": "2026-09-26T10:00:00Z"
    }
  ],
  "observedAt": "2026-09-26T10:00:00Z"
}
```

`status`: `official | external | missing`; `type`: `official | external`. Для `missing`: `value: null`, `sources: []`, `observedAt: null`. Основное отображаемое значение выбирается по правилу приоритета официального источника; альтернативы доступны в `alternatives` того же поля.

Структура ответа:

```json
{
  "id": "ad2dc9d7-c64d-4d5e-9a60-a954c057a570",
  "name": { "value": "Пример поставщика", "status": "official", "sources": [{ "url": "https://example.com/about", "title": "О компании", "excerpt": "Компания «Пример поставщика» поставляет ягоды оптом", "type": "official", "retrievedAt": "2026-09-26T10:00:00Z" }], "observedAt": "2026-09-26T10:00:00Z", "alternatives": [] },
  "description": { "value": "Оптовые поставки ягод", "status": "official", "sources": [{ "url": "https://example.com/about", "title": "О компании", "excerpt": "Оптовые поставки ягод", "type": "official", "retrievedAt": "2026-09-26T10:00:00Z" }], "observedAt": "2026-09-26T10:00:00Z", "alternatives": [] },
  "address": { "value": null, "status": "missing", "sources": [], "observedAt": null, "alternatives": [] },
  "city": { "value": "Екатеринбург", "status": "official", "sources": [{ "url": "https://example.com/contacts", "title": "Контакты", "excerpt": "Наш офис: Екатеринбург", "type": "official", "retrievedAt": "2026-09-26T10:00:00Z" }], "observedAt": "2026-09-26T10:00:00Z", "alternatives": [] },
  "serviceRegions": [],
  "contacts": { "phones": [], "emails": [], "website": { "value": "https://example.com", "status": "official", "sources": [{ "url": "https://example.com/contacts", "title": "Контакты", "excerpt": "Официальный сайт example.com", "type": "official", "retrievedAt": "2026-09-26T10:00:00Z" }], "observedAt": "2026-09-26T10:00:00Z", "alternatives": [] } },
  "products": [],
  "delivery": {
    "terms": { "value": null, "status": "missing", "sources": [], "observedAt": null, "alternatives": [] },
    "maxDays": { "value": 5, "status": "external", "sources": [{ "url": "https://catalog.example/supplier", "title": "Карточка компании", "excerpt": "Доставка до пяти дней", "type": "external", "retrievedAt": "2026-09-26T10:00:00Z" }], "observedAt": "2026-09-26T10:00:00Z", "alternatives": [] }
  },
  "minimumOrder": { "value": null, "status": "missing", "sources": [], "observedAt": null, "alternatives": [] },
  "certificates": [],
  "images": [],
  "sources": [
    { "url": "https://example.com/about", "title": "О компании", "excerpt": "Оптовые поставки ягод", "type": "official", "retrievedAt": "2026-09-26T10:00:00Z" },
    { "url": "https://example.com/contacts", "title": "Контакты", "excerpt": "Наш офис: Екатеринбург", "type": "official", "retrievedAt": "2026-09-26T10:00:00Z" },
    { "url": "https://catalog.example/supplier", "title": "Карточка компании", "excerpt": "Доставка до пяти дней", "type": "external", "retrievedAt": "2026-09-26T10:00:00Z" }
  ],
  "isFavorite": false,
  "note": null,
  "createdAt": "2026-09-26T10:00:00Z",
  "updatedAt": "2026-09-26T10:00:00Z",
  "lastDiscoveredAt": "2026-09-26T10:00:00Z"
}
```

У каждого значения со статусом `official`/`external` должен быть хотя бы один источник с URL и непустым `excerpt` — коротким фрагментом страницы, который подтверждает именно это значение. Типы элементов: `serviceRegions[]`, `certificates[]`, `contacts.phones[]`, `contacts.emails[]`, `images[]` — `SourcedValue<string>`; `products[]` — объект `{name: SourcedValue<string>, category: SourcedValue<string>, prices: Price[]}`. `Price` содержит `amountMin`, `amountMax`, `currency`, `unit`, `isApproximate` и `evidence: SourcedValue<string>`; `evidence.value` — исходная формулировка цены. `minimumOrder.value` — `{amount: string, unit: string}`. `sources[]` верхнего уровня объединяет уникальные ссылки поставщика. `alternatives[]` содержит другие `SourcedValue<T>` без вложенных `alternatives`.

### `PUT /suppliers/{id}/favorite`

Тело `{"isFavorite": true}`. Ответ `200 {"id":"...","isFavorite":true}`. Повтор того же запроса не меняет результат.

### `PUT /suppliers/{id}/note`

Заметка общая для пользователей общей базы. Тело `{"note":"Позвонить во вторник"}` или `{"note":null}` для удаления. Ответ `200 {"id":"...","note":"Позвонить во вторник"}`.

## 6. Согласованность и ограничения

- Все URL источников и изображений должны быть абсолютными `http`/`https`; локальные и служебные адреса отклоняются при сохранении.
- ИИ не задаёт `isFavorite`, `note`, внутренние ID или статус `official`; эти значения определяет приложение.
- Поля, не подтверждённые официальным сайтом, не скрываются, но всегда имеют `external`.
- Пустая база: `items: []`, `totalItems: 0`, `totalPages: 0`.
- Новая версия несовместимого контракта получает новый префикс `/api/v2`.
