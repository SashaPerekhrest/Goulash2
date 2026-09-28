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
      "models": ["sonar", "sonar-pro", "sonar-deep-research", "sonar-reasoning-pro"],
      "basePrompt": "...", "defaultBasePrompt": "...",
      "profilePrompt": "...", "defaultProfilePrompt": "..." }
  ]
}
```

`provider-id` — идентификатор реализованного адаптера, не произвольный URL. `models` содержит точные поддерживаемые идентификаторы; API отклоняет неизвестную модель как `UNSUPPORTED_MODEL`. У Perplexity по умолчанию рекомендуется `sonar-pro`.

Подключены Perplexity Sonar API (`POST https://api.perplexity.ai/v1/sonar`) и Polza.ai, авторизация Bearer API key. Поиск поставщиков состоит из широкого первого запроса со списком кандидатов и отдельного запроса обогащения для каждого сайта. Perplexity получает `search_domain_filter` для домена кандидата; Polza передаёт `site:<domain>` в web plugin. Из `search_results[]` и URL-цитат сохраняются URL, заголовок, доступный сниппет и дата получения. Backend не сверяет значение факта с текстом сниппета; URL и сниппет — контекст, помогающий вручную перейти к источнику. Проверяются JSON, длины, типы данных и публичная схема HTTP(S)-URL.

Адаптер реализует `Goulash.Application.IAiProviderAdapter`: идентификатор, отображаемое имя, признак веб-поиска, поддерживаемые модели и `CheckConnectionAsync(model, apiKey, cancellationToken)`, возвращающий соединение и доступность веб-поиска. В список попадают только зарегистрированные сервером адаптеры с `SupportsWebSearch=true`.

Фоновый обработчик загружает активные настройки и выполняет два этапа. Первый запрос получает текстовый запрос и весь набор содержательных фильтров и возвращает до 20 кратких лидов: название и сайт. Фильтры — инструкция модели при поиске этого списка; сервер не применяет их повторно к профилям. Для каждого лида backend загружает до восьми подходящих публичных HTML-страниц того же сайта, затем отправляет отдельный веб-запрос с найденным текстом страниц. Ответ второго этапа — единый JSON-профиль: описание, адреса, контакты, товары и цены, минимальный заказ, доставка, регионы, сертификаты и изображения. Профили обрабатываются параллельно, максимум по три одновременно. Найденные профили сохраняются напрямую как факты со статусом `aiGenerated`; факт не сверяется со сниппетом. Ошибка одного профиля учитывается отдельно и не отменяет остальные.

### `GET /ai/settings`

```json
{
  "providerId": "provider-id",
  "model": "model-id",
  "routeProvider": null,
  "basePrompt": "...",
  "profilePrompt": "...",
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
  "apiKey": "new-secret",
  "basePrompt": "Instructions for both search stages",
  "profilePrompt": "Profile extraction template"
}
```

Ответ `200` в форме `GET /ai/settings`. `basePrompt` применяется к обоим этапам; `profilePrompt` задаёт редактируемый пользовательский запрос для извлечения профиля на втором этапе. В `profilePrompt` доступны шаблоны `{{supplierName}}`, `{{websiteUrl}}` и `{{websitePages}}`; сервер подставляет данные поставщика и загруженные страницы. Оба промпта ограничены 8000 символами. `apiKey` можно опустить для сохранения прежнего ключа **только при неизменном `providerId` и наличии ранее сохранённого ключа**. При смене провайдера или после удаления ключа новый ключ обязателен (`409 API_KEY_REQUIRED`). Пустая строка недопустима. Неизвестный или не поддерживающий веб-поиск адаптер отклоняется (`400 UNSUPPORTED_PROVIDER`). Ключ шифруется AES-256-GCM с Base64 32-байтовым секретом окружения `AI_ENCRYPTION_KEY` (`Ai:EncryptionKey`) до записи в базу.

### `DELETE /ai/settings/key`

Удаляет сохранённый ключ, оставляя выбранные провайдер и модель. Ответ `204`; поиск после этого недоступен.

### `POST /ai/settings/check`

Выполняет минимальную проверку текущих настроек и доступа к веб-источникам. Ответ `200`:

```json
{ "connected": true, "webSearchAvailable": true, "checkedAt": "2026-09-26T10:00:00Z" }
```

При ошибке — Problem Details с кодом `PROVIDER_NOT_CONFIGURED`, `PROVIDER_UNAVAILABLE`, `PROVIDER_INVALID_RESPONSE` или `PROVIDER_TIMEOUT`. Ключ в запросе проверки не передаётся. Проверка выполняет минимальный веб-запрос через `CheckConnectionAsync` и возвращает успех, когда адаптер отвечает и сообщает доступность веб-поиска. Ошибка не изменяет сохранённые настройки.

## 4. Поиск новых поставщиков

### `POST /discoveries`

Ставит поиск в очередь и немедленно возвращает `202 Accepted`. Хотя бы непустой после обрезки пробелов `query` либо один содержательный фильтр обязателен. Содержательные фильтры: непустые `city`, `region`, `category`, `product`, хотя бы одна граница `price`, `maxDeliveryDays` или хотя бы одна граница минимального заказа. Флаг `includeApproximatePrices`, валюта и единица без ценовой границы сами по себе поиск не запускают.

Тело запроса и правила проверки `filters` приведены ниже. Максимум длины `query` — 500 символов.

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

Ответ `202`:

```json
{ "discoveryId": "f2613141-f43f-4208-9c49-25f98c2ef613", "status": "queued" }
```

### `GET /discoveries/{discoveryId}`

Frontend опрашивает этот маршрут, пока статус `queued` или `running`. Ответ содержит этап и прогресс кандидатов. Когда задание завершено, `result` содержит готовый результат в форме ниже; при ошибке `result` равен `null`, а `errorCode` содержит код Problem Details.

```json
{
  "discoveryId": "f2613141-f43f-4208-9c49-25f98c2ef613",
  "status": "running",
  "stage": "enriching",
  "candidateCount": 12,
  "completedCandidates": 4,
  "acceptedCount": 0,
  "failedProfileCount": 1,
  "errorCode": null,
  "result": null
}
```

`status`: `queued | running | succeeded | failed`; `stage`: `queued | searching | enriching | saving | completed | failed`. При запуске worker возвращает оставленные в очереди задания и повторно обрабатывает прерванные задания. Повторная обработка после сбоя может повторно вызвать провайдера.

При успешном завершении `result` имеет вид:

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
  "failedProfileCount": 0,
  "updatedExistingCount": 0,
  "outcome": "complete",
  "sourcePageCount": 8
}
```

`candidateCount` — число лидов из первого ответа, `completedCandidates` — число завершённых запросов обогащения, `acceptedCount` — число карточек, сохранённых к текущему моменту, `failedProfileCount` — число профилей, которые не удалось получить или обработать. `outcome`: `no_candidates` — первый запрос не вернул лидов; `complete` — обработаны все профили; `partial` — часть профилей завершилась ошибкой; `profiles_failed` — не удалось получить ни одного профиля. Дубликат известного поставщика обновляет его запись, а несколько лидов, совпавших с одной записью, не создают дополнительных карточек.

`items` содержит сохранённые карточки; верхний предел первого списка и результата — 20. `acceptedCount` равен числу карточек, `updatedExistingCount` — числу ранее известных поставщиков среди них. `sourcePageCount` — число страниц сайта и цитат второго этапа, сохранённых вместе с запуском. Неполный профиль не отклоняется: отсутствующие значения остаются отсутствующими. Сервер разбирает JSON для переноса в модель данных и нормализует значения для индексируемых проекций, но не проверяет достоверность фактов и не фильтрует профили по пользовательским условиям.

Проверяются только технические границы: корректный JSON/тип оболочки, безопасные публичные HTTP(S)-URL поставщика и страниц, лимиты объёма скачиваемой страницы и схема выходного JSON. Фильтры передаются только первому запросу и влияют на выбор списка, но модель не гарантирует, что каждый найденный профиль удовлетворит условиям. `aiGenerated` явно обозначает сведения, собранные моделью и не проверенные отдельно. URL страниц и доступные сниппеты сохраняются на уровне поставщика как справочные ссылки, не как подтверждение каждого факта.

Параметры цены и минимального заказа должны быть валидными десятичными числами. Для `price` при наличии хотя бы одной границы обязательны `currency` и `unit`. Для `minMinimumOrder` и `maxMinimumOrder` обязательно указать `amount` и `unit`; единицы двух границ должны совпадать. Нижняя граница не превышает верхнюю. Сроки и границы неотрицательны. `includeApproximatePrices` по умолчанию `false`.

При некорректном теле запрос получает `400 VALIDATION_ERROR`. Ошибки фонового задания доступны в `errorCode`: `PROVIDER_NOT_CONFIGURED` (`409`), `PROVIDER_UNAVAILABLE`/`PROVIDER_INVALID_RESPONSE` (`502`), `PROVIDER_TIMEOUT` (`504`). Неудачное задание имеет статус `failed`; сведения поставщиков при этом не изменяются.

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

`status`: `official | external | aiGenerated | missing`; `type`: `official | external`. Для `missing`: `value: null`, `sources: []`, `observedAt: null`. Факты нового поиска имеют статус `aiGenerated` и обычно не имеют индивидуальных `sources`; страницы и цитаты возвращаются общим списком на уровне поставщика. Они не считаются подтверждением отдельного значения. У ранее сохранённых фактов могут оставаться статусы `official` и `external`.

Структура ответа:

```json
{
  "id": "ad2dc9d7-c64d-4d5e-9a60-a954c057a570",
  "name": { "value": "Пример поставщика", "status": "aiGenerated", "sources": [], "observedAt": "2026-09-26T10:00:00Z", "alternatives": [] },
  "description": { "value": "Оптовые поставки ягод", "status": "aiGenerated", "sources": [], "observedAt": "2026-09-26T10:00:00Z", "alternatives": [] },
  "address": { "value": null, "status": "missing", "sources": [], "observedAt": null, "alternatives": [] },
  "city": { "value": "Екатеринбург", "status": "aiGenerated", "sources": [], "observedAt": "2026-09-26T10:00:00Z", "alternatives": [] },
  "region": { "value": null, "status": "missing", "sources": [], "observedAt": null, "alternatives": [] },
  "serviceRegions": [],
  "contacts": { "phones": [], "emails": [], "website": { "value": "https://example.com", "status": "aiGenerated", "sources": [], "observedAt": "2026-09-26T10:00:00Z", "alternatives": [] } },
  "products": [],
  "delivery": {
    "terms": { "value": null, "status": "missing", "sources": [], "observedAt": null, "alternatives": [] },
    "maxDays": { "value": 5, "status": "aiGenerated", "sources": [], "observedAt": "2026-09-26T10:00:00Z", "alternatives": [] }
  },
  "minimumOrder": { "value": null, "status": "missing", "sources": [], "observedAt": null, "alternatives": [] },
  "certificates": [],
  "images": [],
  "sources": [
    { "url": "https://example.com/about", "title": "О компании", "excerpt": "Оптовые поставки ягод", "type": "external", "retrievedAt": "2026-09-26T10:00:00Z" },
    { "url": "https://example.com/contacts", "title": "Контакты", "excerpt": "Наш офис: Екатеринбург", "type": "external", "retrievedAt": "2026-09-26T10:00:00Z" },
    { "url": "https://catalog.example/supplier", "title": "Карточка компании", "excerpt": "Доставка до пяти дней", "type": "external", "retrievedAt": "2026-09-26T10:00:00Z" }
  ],
  "isFavorite": false,
  "note": null,
  "createdAt": "2026-09-26T10:00:00Z",
  "updatedAt": "2026-09-26T10:00:00Z",
  "lastDiscoveredAt": "2026-09-26T10:00:00Z"
}
```

Каждое значение содержит `value`, `status`, `sources`, `observedAt` и `alternatives`. Для `aiGenerated` `sources` обычно пуст; общий `sources[]` верхнего уровня содержит страницы сайта и цитаты, полученные при поиске. Типы элементов: `serviceRegions[]`, `certificates[]`, `contacts.phones[]`, `contacts.emails[]`, `images[]` — `SourcedValue<string>`; `products[]` — объект `{name: SourcedValue<string>, category: SourcedValue<string>, prices: Price[]}`. `Price` содержит `amountMin`, `amountMax`, `currency`, `unit`, `isApproximate` и `evidence: SourcedValue<string>`; `evidence.value` содержит исходную формулировку, если модель её вернула. `minimumOrder.value` содержит nullable `amount`, `unit` и `details`, поэтому исходное текстовое условие сохраняется и тогда, когда его нельзя разложить на число и единицу измерения. `alternatives[]` доступны у значений, собранных при других запусках.

### `PUT /suppliers/{id}/favorite`

Тело `{"isFavorite": true}`. Ответ `200 {"id":"...","isFavorite":true}`. Повтор того же запроса не меняет результат.

### `PUT /suppliers/{id}/note`

Заметка общая для пользователей общей базы. Тело `{"note":"Позвонить во вторник"}` или `{"note":null}` для удаления. Ответ `200 {"id":"...","note":"Позвонить во вторник"}`.

## 6. Согласованность и ограничения

- Все URL источников и изображений должны быть абсолютными `http`/`https`; локальные и служебные адреса отклоняются при сохранении.
- ИИ не задаёт `isFavorite`, `note`, внутренние ID или статус `official`; эти значения определяет приложение.
- Факты нового поиска имеют `aiGenerated`; система не подтверждает их отдельной страницей.
- Пустая база: `items: []`, `totalItems: 0`, `totalPages: 0`.
- Новая версия несовместимого контракта получает новый префикс `/api/v2`.
