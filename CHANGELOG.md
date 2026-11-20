# Changelog

## 1.4.0.0

- New: **Check a movie** on the Up Next settings page. Pick any movie or episode to see which series Up Next found, the play order, what plays next, and when the card appears and why.
- New: when Intro Skipper hasn't found the credits, Up Next uses a chapter named like "Credits", "End Credits" or "End Titles" if the file has one, instead of a flat 3 minutes before the end.

## 1.3.0.0

- New: film series work without any Jellyfin collections. Up Next now also uses the TMDb series recorded in each movie's metadata, which Jellyfin stores even when "Automatically add to collection" is off (the default). So Resident Evil → Apocalypse → Extinction → … plays in order on a stock setup. Collections still come first when they exist.
- Changed: the default countdown is now 30 seconds. Existing installs keep whatever is saved; change it under Dashboard → Plugins → Up Next.

## 1.2.1.0

- Fixed: with more than 200 shows, similar-show picks only came from the first 200 alphabetically. Now a random sample of matching genres is ranked, like movies.
- Fixed: finding shows you've started loaded your whole watch history; it now loads one entry per show, so series finales stay fast on big libraries.
- Fixed: on some playback setups, the Up Next card could appear at the very start of a video if the server hadn't recorded a position yet.
- Fixed: the end screen could stay on top of a movie you started yourself, especially when rewatching the same one.

## 1.2.0.0

- New: Up Next for TV shows, on the last episode only. Episode-to-episode autoplay is still Jellyfin's own.
  - Series finale (the show has ended): counts down to a similar show you haven't started, from Season 1, Episode 1, and suggests movies that share a collection with the show.
  - Caught up (the show is still airing): "You're all caught up" with suggestions, never autoplay.
- Roku, Android TV and other apps also get the next show after a finale (not when caught up), and it counts toward "Are you still watching?".

## 1.1.0.0

- New: "Are you still watching?" After 3 movies autoplay in a row with nobody touching anything (adjustable, or off), Up Next asks before starting the next one. On apps that can't show the question, such as Roku and Android TV, the server just doesn't start another.
- New: series-aware suggestions. A suggestion that's part of a collection now offers the first part you haven't watched, instead of jumping into the middle.

## 1.0.1.0

- Fixed: opening Up Next settings from the card restarted the countdown instead of pausing it.
- Added a plugin icon for the Jellyfin catalog.

## 1.0.0.0

- First release: Netflix-style Up Next for movies.
- At the credits (from Intro Skipper, or a set time before the end), the video shrinks to the corner and the next movie counts down.
- Collections play in release order; other movies get a similar unwatched pick.
- Play Now, Back to Browse, other suggestions, and Watch Credits by clicking the small video.
- TV remote support, per-user settings, admin settings page.
- Server-side autoplay for apps that can't show the overlay (Roku, Android TV, etc.).
