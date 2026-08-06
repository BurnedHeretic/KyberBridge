import 'package:grpc/grpc.dart' hide Client;
import 'package:kyber/gen/Proto/kyber_interface.pbgrpc.dart';

class ClientGRPCService {
  ClientGRPCService(this.host, this.port, {Duration? connectTimeout}) {
    channel = ClientChannel(
      host,
      port: port,
      options: ChannelOptions(
        credentials: ChannelCredentials.insecure(),
        connectTimeout: connectTimeout,
      ),
    );
    commonClient = CommonClient(channel);
    serverClient = ServerClient(channel);
    client = Client(channel);
  }

  late ClientChannel channel;
  late Client client;
  late CommonClient commonClient;
  late ServerClient serverClient;

  final String host;
  final int port;
}
