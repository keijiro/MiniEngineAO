MiniEngineAO
------------

![screenshot](http://i.imgur.com/Ao7175jm.png)
![screenshot](http://i.imgur.com/k71J9Qym.png)

![screenshot](http://i.imgur.com/guZd8Udm.png)
![screenshot](http://i.imgur.com/Ixuu5Cnm.png)

**MiniEngineAO** is a SSAO image effect for Unity that was originally developed
by Team Minigraph at Microsoft for their [MiniEngine] library.

MiniEngineAO has several advantages compared to other SSAO implementations --
smooth results, good temporal characteristics, optimized for GPU compute. The
most significant advantage may be speed. It's heavily optimized with the
compute shader features, especially with the local memory (TGSM/LDS) use, so
that it manages to avoid major bottlenecks that can be found in traditional
SSAO implementations.

The original design of MiniEngineAO can be explained as a combination of two
known SSAO methods: [Volumetric Obscurance] and [Multi-Scale Ambient Occlusion].
It's well-tailored to have the advantages of both these methods.

System Requirements
-------------------

- Unity 2017.1.0 or later.
- [Compute shader] and [texture array] support.

Although Metal (macOS/iOS) is thought to fulfill the requirements, it doesn't
work due to a texture array issue ([case 926975]). This issue has been already
fixed in the development branch, so it'll be resolved in a near future release.

Installation
------------

Download one of the unitypackage files from the [Releases] page and import it
to a project.

License
-------

MiniEngineAO inherits the original license of the MiniEngine library. See
[LICENSE] for further details.

[MiniEngine]: https://github.com/Microsoft/DirectX-Graphics-Samples
[Volumetric Obscurance]: http://www.cs.utah.edu/~loos/publications/vo/vo.pdf
[Multi-Scale Ambient Occlusion]: https://www.comp.nus.edu.sg/~lowkl/publications/mssao_visual_computer_2012.pdf
[Compute shader]: https://docs.unity3d.com/Manual/ComputeShaders.html
[texture array]: https://docs.unity3d.com/ScriptReference/SystemInfo-supports2DArrayTextures.html
[case 926975]: https://issuetracker.unity3d.com/issues/metal-unable-to-access-texture2darray-from-compute-shader
[Releases]: https://github.com/keijiro/MiniEngineAO/releases
[LICENSE]: https://github.com/keijiro/MiniEngineAO/blob/master/LICENSE
