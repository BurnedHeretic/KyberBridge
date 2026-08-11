<img width="450" height="450" alt="KyberBridge" src="https://github.com/user-attachments/assets/0623e2ab-fc3b-4f3c-8c0b-fc0713b44b99" />

- # KyberBridge

**KyberBridge** is a companion launcher and automation tool for **STAR WARS™ Battlefront™ II** private Kyber servers.

It can be used in two ways:

1. **One-click Kyber server creator**  
   Launch Battlefront II through Kyber, create a private server, load the selected map/mode/mods, add bots, start the match, and close everything automatically when the battle ends.

2. **Galactic Conquest battle component**  
   Used by a Galactic Conquest campaign app to launch battles, load planet-specific mods, detect match results, return the winner to the galactic map, and continue the campaign loop.

---

## Features

### One-click Kyber server creation

KyberBridge can automatically:

- Launch the Kyber Launcher.
- Launch Battlefront II through Kyber.
- Wait for the game/frontend to be ready.
- Create a Kyber private server.
- Select the correct map and mode.
- Load `.fbmod` / `.fbcollection` files through Kyber raw mods.
- Add max bots (For Starfighter Assault & Supremacy) to both teams.
- Start the match.
- Watch Kyber logs for the battle result.
- Write the result to `BattleResult.json`.
- Close Battlefront II and Kyber after the match ends.
- Return focus back to the Galactic Conquest app (if running).

This means a user can start a full Kyber-hosted battle from an external app without manually creating the server each time.

---

## Galactic Conquest integration

KyberBridge is designed to be launched by a separate Galactic Conquest campaign application, found here: https://www.nexusmods.com/starwarsbattlefront22017/mods/14259

The campaign app writes a `BattleData.json` file, then starts `KyberBridge.exe`.

KyberBridge reads that file and uses it to determine:

- Planet
- Battle type
- Map
- Mode
- Era/factions
- Planet mod
- Global user mods
- Bot setup
- Player team assignment
- Battle result handling

After the match ends, KyberBridge writes `BattleResult.json`, which the Galactic Conquest app can read to update the galaxy map.

---

Credit to the Kyber team for the Kyber launcher and for making this possible!
Kyber source can be found here: https://github.com/ArmchairDevelopers/Kyber
