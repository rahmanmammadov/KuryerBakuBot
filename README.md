# Kuryer Baku Moderation Bot 🤖

A professional Telegram moderation bot built in C# and .NET 10. This bot is specifically designed to enforce media posting limits (photos and videos) in active group chats, preventing spam while ensuring a clean user experience.

Developed natively for **Kuryer Baku**, but fully configurable for any Telegram group chat.

## Features 🌟

* **Rule 1 — Album Limit (Multi-Message Post):**
  * If a user uploads a single Telegram album containing 3 or more media files (photos/videos combined), the bot deletes the entire album and issues exactly one warning.
  * Deleted albums do not count towards the user's active media window allowance.
* **Rule 2 — User Media Window (Incremental Posts):**
  * Users can publish a maximum of 2 media files within a configurable time window (e.g., 60 seconds).
  * If a user sends a 3rd media file within that active window, the bot deletes **only** the violating 3rd file, keeping the previous 2 allowed media files.
* **Rule 3 — Warning Spam Prevention:**
  * When a user violates the limit, the bot sends only **one warning** per active window. Additional violating files are deleted silently.
  * When the window expires, the user's warning state and media counter are reset automatically.
* **Rule 4 — Administrator Exclusions:**
  * The group owner and all administrators are completely ignored by the moderation engine.
* **High Performance & Scaling:**
  * **In-Memory Album Tracking:** Album states are kept temporarily in memory (`IMemoryCache`) with sliding expirations to prevent disk overhead and SQLite bloat.
  * **Thread Serialization:** Uses thread locks (`SemaphoreSlim`) per user to prevent race conditions during rapid concurrent posts (such as simultaneous album uploads).
  * **Admin Caching:** Administrator lists are cached for 5 minutes in memory to prevent hitting Telegram API rate limits.

---

## Technical Stack 🛠️

* **Language:** C#
* **Framework:** .NET 10 (Targeting `net10.0`)
* **Core Library:** `Telegram.Bot` (v22.3.0)
* **Hosting Model:** .NET Generic Host (`Microsoft.Extensions.Hosting`)
* **Database:** SQLite (`Microsoft.Data.Sqlite`)
* **Dependency Injection:** Microsoft.Extensions.DependencyInjection
* **In-Memory Caching:** Microsoft.Extensions.Caching.Memory

---

## How to Run Locally 💻

### Prerequisites
* Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
* Install [Git](https://git-scm.com/)

### Setup Instructions
1. Clone this repository to your local machine:
   ```bash
   git clone https://github.com/rahmanmammadov/KuryerBakuBot.git
   cd KuryerBakuBot
