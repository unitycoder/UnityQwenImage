# Unity Qwen Image

Unity Editor tool for generating and editing images locally with Qwen-Image-2.1 through `stable-diffusion.cpp`.

The tool can:

* Generate images from text prompts
* Edit an existing `Texture2D`
* Use the current Scene View as the source image
* Use the Game View or a Camera as the source image
* Apply an optional black and white edit mask
* Save generated images into the Unity project
* Assign the result directly to a Material, Renderer, RawImage, Image, or SpriteRenderer
* Start and stop the local Qwen server directly from Unity

All inference runs locally on your PC. After the required files have been downloaded, image generation does not require an internet connection.

## Requirements

Current setup is intended for:

* Windows 64-bit
* NVIDIA GPU
* Recent Unity version
* `stable-diffusion.cpp` CUDA 12 build
* Qwen-Image-2.1 model files

A GPU with plenty of VRAM is recommended. The included default paths use the smaller Q4_0 diffusion model.

## 1. Install the Unity tool

Place `QwenImageEditorWindow.cs` somewhere inside an Editor folder, for example:

```text
Assets/
    UnityLibrary/
        QwenImage/
            Editor/
                QwenImageEditorWindow.cs
```

The window is available from:

```text
Tools -> UnityLibrary -> Qwen Image
```

## 2. Download stable-diffusion.cpp

Go to the `leejet/stable-diffusion.cpp` GitHub Releases page.

From the Windows release assets, download:

```text
sd-master-<build>-bin-win-cuda12-x64.zip
cudart-sd-bin-win-cu12-x64.zip
```

Extract both archives into:

```text
<ProjectRoot>/Tools/stable-diffusion.cpp/
```

The folder should contain `sd-server.exe` together with the required DLL files.

Example:

```text
Tools/
    stable-diffusion.cpp/
        sd-server.exe
        stable-diffusion.dll
        ggml-cuda.dll
        ggml-cpu-haswell.dll
        cudart64_*.dll
        cublas64_*.dll
        cublasLt64_*.dll
        ...
```

If `sd-server.exe` reports missing CUDA DLL files, make sure the contents of the `cudart` archive were extracted into the same folder.

## 3. Download the model files

Create:

```text
<ProjectRoot>/Tools/models/
```

Download the following files.

### Qwen-Image-2.1 diffusion model

Recommended starting file:

```text
qwen-image-2.1-Q4_0.gguf
```

Download it from the `abenzerps/Qwen-Image-2.1-GGUF` repository on Hugging Face.

Other quantizations such as Q4_K_M, Q6_K, or Q8_0 can also be used if you have enough VRAM.

### Qwen3-VL text encoder

Download:

```text
Qwen3VL-8B-Instruct-Q4_K_M.gguf
```

from the official `Qwen/Qwen3-VL-8B-Instruct-GGUF` Hugging Face repository.

### Qwen3-VL vision projector

Download:

```text
mmproj-Qwen3VL-8B-Instruct-F16.gguf
```

from the same Qwen3-VL repository.

This file is required for image-input/editing workflows with the GGUF Qwen3-VL encoder.

### Qwen-Image-2.1 VAE

Download:

```text
qwen_image_2.1_vae_bf16.safetensors
```

It is available from the `abenzerps/Qwen-Image-2.1-GGUF` repository under its VAE files.

## 4. Final folder structure

Your Unity project should look approximately like this:

```text
<ProjectRoot>/
    Assets/
        UnityLibrary/
            QwenImage/
                Editor/
                    QwenImageEditorWindow.cs

    Tools/
        stable-diffusion.cpp/
            sd-server.exe
            stable-diffusion.dll
            ggml-cuda.dll
            ggml-cpu-haswell.dll
            ...

        models/
            qwen-image-2.1-Q4_0.gguf
            Qwen3VL-8B-Instruct-Q4_K_M.gguf
            mmproj-Qwen3VL-8B-Instruct-F16.gguf
            qwen_image_2.1_vae_bf16.safetensors
```

Do not put the model files under `Assets`. They are large and Unity does not need to import them.

## 5. Start the tool

Open:

```text
Tools -> UnityLibrary -> Qwen Image
```

The default paths already point to:

```text
Tools/stable-diffusion.cpp/sd-server.exe
Tools/models/qwen-image-2.1-Q4_0.gguf
Tools/models/Qwen3VL-8B-Instruct-Q4_K_M.gguf
Tools/models/mmproj-Qwen3VL-8B-Instruct-F16.gguf
Tools/models/qwen_image_2.1_vae_bf16.safetensors
```

Click:

```text
Start Server
```

Or leave `Auto Start Server` enabled and start generating directly.

The local server runs on:

```text
http://127.0.0.1:1234
```

## 6. Generate an image from text

Set:

```text
Source Mode = None
```

Enter a prompt and click:

```text
Generate From Text
```

Generated images are saved under:

```text
Assets/QwenGenerated/
```

Example prompt:

```text
A photorealistic Finnish summer cottage beside a lake, natural evening light, detailed wooden materials
```

## 7. Edit an existing image

Choose:

```text
Source Mode = Texture
```

Drag a `Texture2D` into the Source Image field.

Enter an instruction such as:

```text
Make this look photorealistic. Preserve the composition and camera angle.
```

Then click:

```text
Modify Source Image
```

## 8. Use Scene View as the source

Choose:

```text
Source Mode = SceneView
```

The tool captures the current Scene View and sends it to Qwen as the source image.

This is useful for turning a Unity blockout or game scene into a more realistic concept image.

Example prompt:

```text
Make this Unity scene look photorealistic. Preserve the geometry, composition and camera angle. Improve materials, vegetation, lighting, shadows and reflections.
```

## 9. Use Game View as the source

Choose:

```text
Source Mode = GameView
```

In Play Mode, the tool captures the current Game View.

Outside Play Mode, assign a Camera to the `Game Camera` field. If no camera is assigned, the tool tries to use `Camera.main`.

## 10. Masks

Enable:

```text
Use Mask
```

and assign a black and white mask texture.

By default:

* White = area to edit
* Black = area to protect

Use `Invert Mask` if you need the opposite behavior.

## 11. Assign the generated image

The generated image can be assigned directly to:

* Material
* Renderer
* RawImage
* Image
* SpriteRenderer

Select a compatible object and click:

```text
Use Current Selection
```

Then use:

```text
Assign Last Result
```

You can also enable `Auto Assign`.

For Materials, the default texture property is:

```text
_BaseMap
```

Change it if your shader uses another property such as `_MainTex`.

## Troubleshooting

### Server executable not found

Make sure this file exists:

```text
Tools/stable-diffusion.cpp/sd-server.exe
```

### CUDA DLL is missing

Extract:

```text
cudart-sd-bin-win-cu12-x64.zip
```

into the same folder as `sd-server.exe`.

### Model loading fails

Make sure each file is used for the correct purpose:

```text
Diffusion model:
qwen-image-2.1-Q4_0.gguf

LLM:
Qwen3VL-8B-Instruct-Q4_K_M.gguf

LLM Vision:
mmproj-Qwen3VL-8B-Instruct-F16.gguf

VAE:
qwen_image_2.1_vae_bf16.safetensors
```

Do not use the Qwen3-VL LLM file as the diffusion model.

### Vision projector or image editing error

Make sure the LLM and `mmproj` files are from the same `Qwen3-VL-8B-Instruct-GGUF` repository.

### Out of VRAM

Try:

* Q4_0 or Q4_K_M diffusion model
* Lower image resolution
* Keep `Offload To CPU` enabled
* Close other GPU-heavy applications

### Port 1234 is already in use

Stop the other `sd-server` process or change the port in the Qwen Image Editor window.

## Privacy and offline use

The Unity tool communicates with `sd-server.exe` through `127.0.0.1`.

Prompts, source images, Scene View captures, Game View captures, and generated images are processed locally.

Internet access is only required to initially download the required binaries and model files.

## Third-party projects and licenses

This repository does not need to include the large model weights or `stable-diffusion.cpp` binaries.

Users should download them directly from their original projects:

* `leejet/stable-diffusion.cpp`
* `Qwen/Qwen-Image-2.1`
* `Qwen/Qwen3-VL-8B-Instruct-GGUF`
* `abenzerps/Qwen-Image-2.1-GGUF`

Check the license of each upstream project and model before redistribution or commercial use.

### Images

<img width="742" height="1324" alt="image" src="https://github.com/user-attachments/assets/c44525a6-c0cf-4335-9bff-bc3d14b8a476" />
