using System;
using System.IO;
using IDSImaging.Peak.API;
using IDSImaging.Peak.API.Core;
using IDSImaging.Peak.API.Core.Nodes;
using IDSImaging.Peak.IPL;
using System.Configuration;
using System.Diagnostics.Eventing.Reader;

namespace CameraScannerApp
{
    class Program
    {
        // ID unique des cameras
        public string[] Serial_list_cam = { "2678", "9872", "0981" };
        static void Main(string[] args)
        {
            try
            {
                Program program = new Program();

                string path_bridge = ConfigurationManager.AppSettings["Bridge"];
                string path_photo = ConfigurationManager.AppSettings["Photo Folder"];

                if (!File.Exists(path_bridge))
                {
                    Console.WriteLine("ERREUR : Le fichier n'existe pas à l'adresse indiquée !");
                    return;
                }

                var lignes = File.ReadAllLines(path_bridge).ToList();

                // On recherche la camera a appeler
                int type_camera = program.numero_cam_reader(lignes[0]);

                program.prise_photo(type_camera, path_photo);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        internal int numero_cam_reader(string fileName)
        {
            // On récupère l'index du dernier '_' et du dernier '.'
            int lastUnderscore = fileName.LastIndexOf('_');
            int lastDot = fileName.LastIndexOf('.');

            // On extrait ce qui se trouve entre les deux
            string valueStr = fileName.Substring(lastUnderscore + 1, lastDot - lastUnderscore - 1);

            // Conversion en int
            int resultat = int.Parse(valueStr);

            Console.WriteLine(resultat); // Affiche 1
            return resultat;
        }

        internal void ack_write(bool ack)
        {
            string path_bridge = ConfigurationManager.AppSettings["Bridge"];
            // Enregistrer toutes les lignes en mémoire
            var lignes = File.ReadAllLines(path_bridge).ToList();

            if (lignes.Count == 2)
            {
                if (ack)
                {
                    lignes[1] = "true";
                }
                else
                {
                    lignes[1] = "false";
                }
                // Sauvegarde des changements
                File.WriteAllLines(path_bridge, lignes);
                return;
            }
            else
            {

            }
        }

        internal void prise_photo(int type_camera, string path_photo)
        {
            try
            {
                Program program = new Program();
                int i = 0;
                // 1. Initialisation
                IDSImaging.Peak.API.Library.Initialize();
                var deviceManager = DeviceManager.Instance();
                deviceManager.Update();
                var devices = deviceManager.Devices();

                // List all available devices.

                Console.WriteLine("Devices available: ");

                foreach (var DeviceDescriptor in devices)
                {
                    // L'ID unique (souvent le numéro de série)
                    string uniqueID = DeviceDescriptor.SerialNumber();
                    // Le nom complet du modèle
                    string modelName = DeviceDescriptor.ModelName();

                    Console.WriteLine($"MODELE : {modelName} | ID UNIQUE : {uniqueID}");
                }

                // 2. Ouverture de la caméra
                // Récupération de l'ID cible
                string serialAttendu = Serial_list_cam[type_camera];

                // --- Étape 2 : Chercher la caméra correspondante ---
                // On filtre la liste des caméras branchées pour trouver CELLE qui a ce Serial exact
                var deviceDescriptor = devices.FirstOrDefault(d => d.SerialNumber() == serialAttendu);

                if (deviceDescriptor == null)
                {
                    Console.WriteLine($"[ERREUR] La caméra attendue (SN: {serialAttendu}) n'est pas détectée sur le port USB.");

                    // On informe l'UI principale que la mission a échoué
                    this.ack_write(false);

                    // ON S'ARRÊTE ICI : Le code suivant (ouverture et capture) ne sera jamais exécuté
                    return;
                }

                // --- Étape 3 : Ouverture uniquement si tout est OK ---
                using var device = deviceDescriptor.OpenDevice(DeviceAccessType.Control);
                Console.WriteLine($"[OK] Caméra {serialAttendu} identifiée et ouverte.");
                Console.WriteLine($"Connecté à : {device.ModelName()}");

                var remoteNodeMap = device.RemoteDevice().NodeMaps()[0];
                using var dataStream = device.DataStreams()[0].OpenDataStream();

                // 3. Allocation de la mémoire 
                var payloadSize = (uint)remoteNodeMap.FindNode<IntegerNode>("PayloadSize").Value();

                // On demande le minimum vital exigé par ta caméra et on ajoute 1 marge de sécurité
                var minBuffers = dataStream.NumBuffersAnnouncedMinRequired();
                for (int iii = 0; iii < minBuffers + 1; iii++)
                {
                    var buffer = dataStream.AllocAndAnnounceBuffer(payloadSize, IntPtr.Zero);
                }

                int photoCount = 1;

                // 5. Boucle de capture
                while (true)
                {
                        try
                        {
                            // A. Nettoyage TOTAL avant la photo
                            dataStream.Flush(DataStreamFlushMode.DiscardAll);
                            foreach (var buf in dataStream.AnnouncedBuffers())
                            {
                                dataStream.QueueBuffer(buf); // On remet tout au propre
                            }

                            // B. On lance la capture
                            dataStream.StartAcquisition();
                            remoteNodeMap.FindNode<CommandNode>("AcquisitionStart").Execute();

                            // C. On attrape la première image qui arrive
                            var finishedBuffer = dataStream.WaitForFinishedBuffer(5000);

                            // D. On coupe l'acquisition IMMÉDIATEMENT
                            remoteNodeMap.FindNode<CommandNode>("AcquisitionStop").Execute();
                            dataStream.StopAcquisition();

                            // E. Conversion et Sauvegarde
                            var iplImage = new IDSImaging.Peak.IPL.Image(
                                (IDSImaging.Peak.IPL.PixelFormatName)finishedBuffer.PixelFormat(),
                                finishedBuffer.BasePtr(), finishedBuffer.Size(),
                                finishedBuffer.Width(), finishedBuffer.Height());

                            var converter = new IDSImaging.Peak.IPL.ImageConverter();
                            var convertedImage = converter.Convert(iplImage, IDSImaging.Peak.IPL.PixelFormatName.RGBa8);

                            IDSImaging.Peak.IPL.ImageWriter.Write(path_photo, convertedImage);

                            Console.WriteLine($"[OK] Photo {photoCount} enregistrée dans {path_photo}");
                            
                            // On écris sur le pont que la photo a bien été prise
                            program.ack_write(true);
                            photoCount++;
                            break;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ERREUR] : {ex.Message}");
                        // On écris sur le pont que la photo a bien été prise
                        program.ack_write(false);
                        // Sécurité vitale en cas de plantage
                        try { remoteNodeMap.FindNode<CommandNode>("AcquisitionStop").Execute(); } catch { }
                            try { dataStream.StopAcquisition(); } catch { }
                        }
                }

                // 6. Rendu de la mémoire à Windows
                foreach (var buf in dataStream.AnnouncedBuffers())
                {
                    dataStream.RevokeBuffer(buf);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur Fatale : {ex.Message}");
            }
            finally
            {
                IDSImaging.Peak.API.Library.Close();
            }
        }
    }
}