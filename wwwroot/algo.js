import * as signalR from "@microsoft/signalr";

const token = "<JWT>";
const estacionId = 11;

const connection = new signalR.HubConnectionBuilder()
  .withUrl("https://<tu-host>/hubs/notificaciones", {
    accessTokenFactory: () => token
  })
  .withAutomaticReconnect()
  .build();

connection.on("ReporteAsignado", payload => {
  console.log("Nuevo reporte asignado:", payload);
});

async function start() {
  await connection.start();
  await connection.invoke("JoinEstacion", estacionId);
  console.log("Suscrito a estación", estacionId);
}

start().catch(console.error);