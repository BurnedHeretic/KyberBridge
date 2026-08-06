import 'dart:io';

import 'package:dio/dio.dart';

class KyberNetworkHelper {
  KyberNetworkHelper._();

  static Future<String> getCurrentIpAddress() async {
    final resp = await Dio().get<String>('https://checkip.amazonaws.com/');
    return (resp.data!).replaceAll('\n', '');
  }

  static Future<int> findAvailablePort({int i = 0}) async {
    ServerSocket? server;
    try {
      server = await ServerSocket.bind('0.0.0.0', 0);
      final port = server.port;

      if (i > 10) {
        throw Exception('No available port found');
      }

      if (port == 0 || port == 443 || port == 80) {
        return findAvailablePort(i: i + 1);
      }

      return port;
    } finally {
      await server?.close();
    }
  }
}
