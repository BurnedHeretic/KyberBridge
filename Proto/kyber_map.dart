class KyberMap {
  KyberMap({
    required this.map,
    required this.name,
    this.isCustom = false,
    this.mode,
  });

  factory KyberMap.fromJson(Map<String, dynamic> json) => KyberMap(
        map: json['map'] as String,
        name: json['name'] as String,
      );

  String map;
  String name;
  String? mode;
  bool isCustom;

  Map<String, dynamic> toJson() => {
        'map': map,
        'name': name,
      };
}
