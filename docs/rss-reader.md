# RSS reader

RSS uses the same `IBackendClient` in the desktop, CLI, Android independent
workspace, and attached daemon. Subscriptions, cached articles, and read/star state
live in the workspace SQLite database. There is no second cache or sync service.

## Implementation plan

1. Add immutable feed/article models and contract methods, then append schema 12
   with transactional writes, exact receipts, revision checks, and restore upgrades.
2. Fetch bounded RSS 2.0 and Atom documents in the owning backend. Keep network I/O
   outside SQLite transactions. Reject unsafe destinations and XML entities.
3. Add a shared, scrollable reader and CLI commands. Expose RSS through Android's
   More menu and persist the RSS navigation destination.
4. Verify parsing, persistence, migration, replay, embedded/daemon/CLI parity,
   desktop screenshots, and an APK through adb on the disposable API 36 emulator.

## Using the reader

Open **RSS** in the desktop sidebar or Android **More → RSS**. Expand **Add
subscription**, paste a feed URL, and choose **Subscribe**. Subscription itself
works offline. Select the feed and choose **Refresh feed** to download articles.
Refresh is manual and runs in the owning backend: on the device for independent
workspaces, or on the daemon for attached workspaces.

Choose **Read cached article** to expand the plain-text feed content. Read status
is explicit: **Mark read / Mark unread**. **Star / Unstar** saves articles for the
starred view. **Open original** launches the HTTP(S) article in the system browser.
That action requires whatever connectivity the publisher needs; it does not download
the whole page into Snook. No remote images, scripts, styles, or enclosures render
inside the reader.

**All feeds** combines active subscriptions. Search matches article titles,
case-insensitively for ASCII; SQLite's built-in case mapping does not implement full
Unicode case folding. Choose unread/starred filters and **Apply filters**. Pages
contain 20 articles. **Delete / restore feed** soft-deletes the selected feed;
**Show deleted** reveals it for restoration with its cached articles and state.
The URL draft survives in-process refresh and rotation; **Clear** discards it.
It is not a process-death recovery editor.

## CLI

Read shortcuts and all six shared contract methods appear in `snook api`:

```bash
snook rss-feeds
snook rss-feeds --include-deleted
snook rss-articles '{"unreadOnly":true,"limit":20}'
snook rss-articles '{"starredOnly":true,"search":"science","offset":0,"limit":20}'
snook call subscribe-rss '{"url":"https://www.rssboard.org/files/sample-rss-2.xml","request":{"operationId":"OPERATION_UUID","clientDeviceId":"DEVICE_UUID"}}'
snook call refresh-rss '{"feedId":"FEED_UUID","request":{"operationId":"OPERATION_UUID","clientDeviceId":"DEVICE_UUID","expectedRevision":1}}'
snook call set-rss-article-state '{"articleId":"ARTICLE_UUID","isRead":true,"isStarred":true,"request":{"operationId":"OPERATION_UUID","clientDeviceId":"DEVICE_UUID","expectedRevision":1}}'
snook call set-rss-feed-deleted '{"feedId":"FEED_UUID","deleted":true,"request":{"operationId":"OPERATION_UUID","clientDeviceId":"DEVICE_UUID","expectedRevision":2}}'
```

Replace UUID placeholders with real UUIDs. Read the current revision before each
change; retain the exact operation ID and arguments when retrying a response-uncertain
mutation. Successful refresh replay uses its saved result without downloading again.
Failed fetches leave the cache and committed cursor unchanged and may be retried.

## Formats and bounds

The parser follows [RSS 2.0](https://www.rssboard.org/rss-specification) and
[Atom 1.0](https://www.rfc-editor.org/rfc/rfc4287.html). It accepts RSS description
or content:encoded, and Atom content or summary, including XHTML converted to text.
It resolves relative article links and inherited xml:base, normalizes valid dates to
UTC, and permits missing/invalid publication dates. GUID/Atom ID identifies an entry;
link or title/content is the fallback. IDs are hashed and scoped to a subscription.
Existing cached content is immutable; later refreshes preserve its read/star revision.
Publisher edits under the same ID do not replace the first cached version.

Limits: 200 subscriptions including deleted subscriptions; 10,000 cached articles
per feed; 500 items and 2 MiB decompressed XML per fetch; five redirects; 25 seconds
for a fetch; 500 characters per title and 32,000 per cached content; 100 articles
per API page. A feed exceeding its article limit fails atomically without eviction.
Subscription URLs are HTTP(S), default ports only, no credentials or fragments;
HTTPS is recommended, and Android retains its platform cleartext restrictions.
Every socket connection resolves and validates the destination and connects to that
validated address. Private, loopback, link-local, tailnet, multicast and IPv6 transition
addresses are blocked. Redirects use the same checks. The fetcher sends no workspace
credentials, cookies, or proxy credentials. XML DTDs/external entities are prohibited.
There is no automatic polling, OPML import/export, enclosure download, full-page
scraping, or background Android fetch job in this version.

## Data and verification

Schema 12 is additive and checksummed. Backups include the RSS tables and exact
receipts; older backups upgrade in staging. JSON export schema 6 adds `rss.feeds`
and `rss.articles`, including deleted subscriptions. CSV worklog export is unchanged.

For desktop review, use fresh disposable paths:

```bash
SNOOK_DATA_DIR=/tmp/snook-rss-review-data \
SNOOK_SCREENSHOT_DIR=/tmp/snook-rss-review-images \
SNOOK_SCREENSHOT_SEED_RSS=1 SNOOK_SCREENSHOT_VERIFY_RSS=1 \
SNOOK_SCREENSHOT_SECTIONS=rss SNOOK_SCREENSHOT_SIZE=980x640 \
./scripts/capture-ui-screenshots.sh
```

Use `SNOOK_SCREENSHOT_SEED=0` and omit RSS seed/verify switches for empty-state
captures. RSS fixture seeding applies only to embedded screenshot workspaces.
For daemon screenshots, populate the disposable server using the CLI and run the
screenshot harness against its saved client profile.

The bounded offline Android check requires an already installed Debug APK on a
**disposable emulator**. In an independent workspace, subscribe/refresh a feed,
mark one article read and starred, and leave RSS open. Then run:

```bash
python3 scripts/verify-android-rss.py --serial emulator-5554 \
  --adb /tmp/snook-android-sdk/platform-tools/adb \
  --output artifacts/rss/android-offline-new
```

It refuses physical devices, temporarily disables emulator networking, restarts
only `org.snook.spike`, verifies RSS navigation and cached content, compares persisted
RSS records before/after, and restores the original network switches in `finally`.
It does not clear app data. Evidence includes a JSON report and screenshot.

Development APKs for this implementation are under `artifacts/rss/apk/`: x64 for
the emulator and ARM64 for installation on matching devices. They use development
signing; production distribution and physical-device testing are outside this task.
