# Multiuser Environmental Sharing
### A Open source framework for collaboration in hybrid meeting environments!

*Please note that this project is still a Work in Progress! If you encounter issues feel free to open a issue! :)* 

<img width="544" height="463" alt="pov of remote client" src="https://github.com/user-attachments/assets/e83e3472-c8f4-42b0-beae-bb7fa34c81aa"/><br>
<sup>POV of a remote client, viewing the users in the physical space.</sup>

## About

**MUES-Core** is a modular Unity package that enables seamless mixed-reality sessions between on-site and remote teams. It synchronizes physical and virtual spaces to create a shared spatial context.

### How it Works
1.  **Scan:** The local host scans their physical room using the **Meta MR Utility Kit (MRUK)**.
2.  **Sync:** Geometry data is serialized and distributed at runtime to remote clients via **Photon Fusion**.
3.  **Interact:** Remote users appear in an abstract digital twin of the real room. This ensures precise alignment between the remote user's virtual position and the local user's physical Passthrough view.

### Tech Stack
* **Networking:** Photon Fusion (State Transfer)
* **Audio:** Photon Voice (VoIP)
* **Spatial Computing:** Meta MR Utility Kit (MRUK)
<br>

*Future plans include implementing AI-based reconstruction for photorealistic environment sharing.*
<br><br>

This Framework requires the ```Meta All in One SDK``` *(v.83)*,  ```GLTFast (Unity) - com.unity.cloud.gltfast``` as well as ```Photon Fusion``` and ```Photon Voice```.
URP is supported, BRP support is being worked on!

<br>
<img width="420" height="420" alt="pov of colocated client" src="https://github.com/user-attachments/assets/23b2ee76-7f9a-44ab-a338-97568c820f91" /><br>
<sup>POV of a colocated client, viewing the remote client in its position relative to the physical space.</sup>

## Installation

```Install via git url``` inside of the **Unity Package Manager**. --> https://github.com/j0nes-l/MUES-Core.git?path=/package
<br><br>
For correct use, please add the ```MUES-Core.Runtime``` namespace under ```Assemblies to Weave``` in the **Fusion Network Config!**
<br><br>
If you want your avatar or other objects to be visible during loading, assign it the ```MUES_RenderWhileLoading``` Layer!
