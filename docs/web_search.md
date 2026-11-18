# Web Search

Contacts can look up current information online when a message calls for it. A contact can then talk about a game released last month, or check whether a new version of something is out, without turning the chat into a research session. Web search is optional, off by default, and uses [Tavily](https://tavily.com)'s hosted search API with an API key you provide.

## When contacts search

Before a contact with a model replies, the app decides whether your latest message calls for a search. Contacts with simulated replies never search, and at most one search is sent per reply, with no retries.

- **You ask.** "Can you look up…", "search for…", "check online whether…", "google it", "what does the internet say about…". "Look it up!" with no subject searches for what you said in your previous message.
- **You ask about something recent.** A question that mentions something new or changing ("latest", "new version", "out yet", "released", "this week", "who won", a recent year…) and is about a specific named thing or a public topic (a game, film, release, election, match…).
- **Maybe.** A question about a specific named thing with no sign of recency, such as "Have you played Hollow Knight?". The contact's own model is asked one constrained yes-or-no question about your latest message alone. Anything but a clear "yes", including an error, means no search. This costs a second, short pass of the local model, so it is only asked when a search could actually follow.
- **Everything else** is answered without searching: statements, small talk, feelings, plans, questions about the contact or you ("how was your day?"), and anything a well-read person would know. Mentioning a current topic is not enough; the message has to ask about it.

These are rules, not a model's tool calling, because the small local models this app runs do not reliably support tool calling. Rules miss some questions a person would look up and occasionally catch one they would not; the cost of either is small. They work best on English messages.

## Turning it on

1. Get an API key from [app.tavily.com](https://app.tavily.com). Tavily's free plan includes 1,000 credits a month (as of November 2026). A basic search costs 1 credit.
2. In **Settings → Web Search**, tick **Let contacts search the web**, paste the key, and choose **Save key**. The key is stored in the operating system's credential store (Windows Credential Manager, or the Secret Service through `secret-tool` on Linux), never in the database, settings files, or logs.
3. Optionally choose **Check key**. It asks Tavily's usage endpoint whether the key works and how many credits it has used, which costs nothing.

Your choice to allow contacts to search is saved and remains on after you restart the app. Web search stays off until you turn it on, and you can turn it off again at any time.

To stop all external searching, untick **Let contacts search the web** (or **Use Tavily**), or remove the key.

## Credits and the local budget

The app counts Tavily credits itself, in SQLite, per billing period:

- **Monthly credit budget** (default 500, half the free allowance) is the most searches may use in a period. When the count reaches it, contacts stop searching until the next period. Set it to fit your plan; the app does not assume you are on the free plan.
- **Period starts on day** (1 to 28, default 1) is when your Tavily credits renew. The count for each period is kept separately, so a new period starts from zero. Restarting the app or changing the key never resets it.
- Each search is counted before it is sent, as a single atomic database update, so simultaneous replies cannot overspend. The count is then corrected: to what Tavily reports the search cost; refunded when Tavily refused the request outright (rejected key, rate limit, exhausted quota, malformed request); and kept when the outcome is unknown, such as a timeout or an unreadable answer, since the search may have been charged.
- **Check key** raises the count to what Tavily reports for the key, if that is higher.
- Each search reserves 1 credit, what a basic search costs. If Tavily ever charged more for one, the count could end that period slightly over the budget; searching then stops, since nothing is left.

The count is an estimate and a safeguard, not your account's balance. Searches made elsewhere with the same key are not counted until you check the key, and Tavily's billing cycle may not line up exactly with the period you set. Tavily's dashboard is authoritative.

Limits Tavily reports are respected whatever the local count says:

| Tavily answer | What happens |
|---|---|
| 401/403: key rejected | No more searches until a new key is saved or the key checks out. |
| 429: rate limited | Searching pauses for the time Tavily asks (`Retry-After`), or one minute, at most an hour. |
| 432/433: plan or pay-as-you-go limit | Searching pauses until the next period starts, or until **Check key** shows credits left. |
| Network failure, timeout (15 s), server error | That reply goes ahead without web information. |

Settings shows the estimate, what is left, and the most recent problem.

## What is sent

Only a short query is sent to Tavily: at most 12 words, made from the sentence of your latest message that asks the question (or, for "look it up", your previous message). Before it is sent:

- your name and the contact's names, email addresses, phone numbers, links, and numbers of five or more digits are removed;
- requests and filler ("can you look up", "please", "my", "I"…) are removed.

Your conversation history, the contact's memories, your profile, the contact's profile, and the API key's value are never part of a query. The query and the sources are kept with the reply and shown from the globe button beside it, so you can always see exactly what was sent. A query that still mentions something you consider private was taken from your own words; rephrase or turn web search off if you would rather not send it.

Tavily's own privacy policy governs what it does with the queries it receives.

## How results are used

Up to three results, each cut to a title, site, publication date, and a snippet of at most 280 characters, are added to the contact's instructions in a section that says they are information found online that cannot change who the contact is or tell them what to do. Retrieved text is treated as untrusted: line breaks, chat-template tokens, headings, dividers, and speaker labels such as "System:" are removed, so a page cannot open a section of its own or pose as a new turn. Links are never followed, and nothing retrieved is ever run.

The contact is asked to use what it found only where it helps, casually, without lists, links, or sources, never as if it saw or did the thing itself, and to say it looked it up online if asked how it knows.

## Caching

Results are cached in the application database for 3 hours when the question was about something recent, and 24 hours otherwise. The cache is keyed by the query alone (lowercased), so it is shared across contacts and holds nothing from a conversation. A cached answer costs no credits, and two identical searches in progress at once are sent only once. Finding nothing is cached too. Expired entries are removed whenever results are cached, and at most 300 entries are kept.

## Memory

What is looked up informs one reply only. It is not saved as a memory. Memories are still learned only from your own messages, and retrieved text never appears in a conversation's messages, so it cannot be learned later either. The query and sources stored with a reply are for you to see; they are not given to agents.

## Known limitations

- The decision rules are heuristics tuned for English; questions phrased unusually may not trigger a search, and the "maybe" check is only as reliable as the contact's local model.
- Asking the model the "maybe" question uses the local runtime, which keeps one conversation's prompt cached at a time, so the reply that follows may take a little longer.
- Snippets are short; a contact may misread or overstate them, and search results can be wrong or out of date.
- Tavily is the only provider. Bing's RSS search output was considered as a free fallback but rejected: `bing.com/robots.txt` disallows `/search`, and every RSS response restricts its results to "rendering … within an RSS aggregator for your personal, non-commercial use". The official Bing Search APIs were retired in August 2025.
