# AR Whiteboard

An Android AR app that puts a shared, persistent drawing layer on the whiteboard in the IMT lounge (5th floor, Universitas Ciputra Makassar). Scan the marker on the real whiteboard, and a virtual board appears over it. Anything drawn on it is saved and shows up for everyone else who scans the same marker.

Built with Unity 6 (URP) and Vuforia Engine, with a Spring Boot server that stores and syncs the drawings.

## Download

Get the latest APK from the [Releases](https://github.com/KeaneKen/ar_whiteboard/releases) page.

## Features

- Draw, erase and clear on a virtual whiteboard anchored to a printed marker
- Pen colours and sizes, and a partial eraser
- Shape tool: square, triangle and circle
- Sticky notes you can write or draw on, pins, and a ruler, all movable by press and hold
- Drawings sync between phones through the board server
- Sound, vibration and visual feedback for every action
- First-time checklist that walks you through the main tools
- Settings: sound, vibration, steady board, reduce motion, aim to draw, toolbar side, frame rate counter and tutorial restart

## Requirements

- Android 10 (API 29) or newer
- The printed marker, about 30 x 30 cm
- The board server running and reachable, for syncing between phones

## How to use

1. Install the APK. Allow installing from unknown sources if Android asks.
2. Open the app and point the camera at the marker.
3. Draw with one finger. Use the pen button to change colour and size, pick a shape, or place notes, pins and the ruler.
4. Press and hold a note, pin or ruler to move it.
5. Open settings with the gear button.

## Running the board server

The server lives in its own repository. It is a Spring Boot app (Java 17, Maven) with an H2 database.

```
mvn spring-boot:run
```

It starts on port 8080. To reach it from phones on other networks, run a Cloudflare quick tunnel:

```
cloudflared tunnel --url http://localhost:8080
```

Copy the `https://...trycloudflare.com` address it prints into the **Server Url** field of the `DrawingBoard` component in Unity, then rebuild the APK.

## Building from source

1. Open the project in Unity 6000.6.5f1.
2. Add your own Vuforia license key in the Vuforia configuration. The key is not included in this repository.
3. Set the Server Url on the `DrawingBoard` component.
4. Switch the platform to Android and build.

## Project structure

| Script | What it does |
|---|---|
| `DrawingBoard.cs` | Drawing, erasing, shapes, toolbar, server sync |
| `BoardObjects.cs` | Sticky notes, pins and ruler |
| `BoardFeedback.cs` | Sounds, vibration and the dust effect |
| `BoardTasks.cs` | First-time checklist |
| `BoardSettings.cs` | Settings panel, steady board, frame rate counter |

## Known limitations

- The server is not hosted permanently. Drawings only sync while the server and its tunnel are running.
- The quick tunnel address changes every time it restarts, so the app has to be rebuilt with the new address.
- Notes, pins and rulers are saved on the phone only and are not synced.
- Tracking can lag on slower phones.

## Credits

- Marker image: board background by Vectorfair J (Canva), chibi Momotaros artwork by Heatnix Rider (DeviantArt)
- Built with Unity, Vuforia Engine, Spring Boot, H2, Maven and cloudflared

Made by Andi Tubagus Faatih Keane Kaylahi Arbini for the XR class at Universitas Ciputra Makassar.
