import 'package:kyber/kyber.dart';

Server KyberExampleServer({String? host}) => Server(
      id: "-",
      official: false,
      name: "Example Server",
      levelSetup: LevelSetup(
        map: 'S5_1/Levels/MP/Geonosis_01/Geonosis_01',
        mode: 'HeroesVersusVillains',
      ),
      requiresPassword: false,
      creator: host ?? "Unknown",
      mods: [
        ServerMod(
          name: "IOI - Instant Online Improvements V5 (5.0)",
        ),
        ServerMod(
          name: "IOI Addon - Heroes Unrestricted (1.0)",
        ),
      ],
    );
