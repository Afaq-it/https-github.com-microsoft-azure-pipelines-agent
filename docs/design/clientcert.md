# Support Ssl Client Certificate in Build/Release Job (TFS On-Prem Only)

## Goals

  - Support agent configure and connect to TFS use ssl client certificate
  - Support get source in Build job and download artifact in Release job works with ssl client certificate
  - Provide documentation and scripts to help customer prepare all pre-requrements before configuration
  - Expose ssl client certificate information in vsts-task-lib for task author

## Pre-requirements

  - CA root certificate in `.pem` format (This should contains the public key and signature of the CA root certificate)  
  - Client certificate in `.pem` format (This should contains the public key and signature of the Client certificate)  
  - Client certificate private key in `.pem` format (This should contains only the private key of the Client certificate)  
  - Client certificate archive package in `.pfx` format (This should contains the signature, public key and private key of the Client certificate)  
  - Use `SAME` password to protect Client certificate private key and Client certificate archive package, since they both have client certificate's private key  
  
The Build/Release agent is just xplat tool runner, base on what user defined in their Build/Release definition, invoke different tools to finish user's job. So the client certificate support is not only for the agent infrastructure but most important for all different tools and technologies user might use during a Build/Release job.
```
   Ex:
      Clone Git repository from TFS use Git
      Sync TFVC repository from TFS use Tf.exe on Windows and Tf on Linux/OSX
      Write customer Build/Release task that make REST call to TFS use VSTS-Task-Lib (PowerShell or Node.js)
      Consume Nuget/NPM packages from TFS package management use Nuget.exe and Npm
      [Future] Publish and consume artifacts from TFS artifact service use Drop.exe (artifact) and PDBSTR.exe (symbol)
```


You can use `OpenSSL` to get all pre-required certificates format ready easily as long as you have all pieces of information.

### Windows

Windows has a pretty good built-in certificate manger, the `Windows Certificate Manager`, it will make most Windows based application deal with certificate problem easily. However, most Linux background application (Git) and technologies (Node.js) won't check the `Windows Certificate Manager`, they just expect all certificates are just a file on disk.  

Use the following step to setup pre-reqs on Windows, assume you already installed your corporation's `CA root cert` into local machine's `Trusted CA Store`, and you have your client cert `clientcert.pfx` file on disk and you know the `password` for it.  
  - Export CA cert from `Trusted CA Store`, use `Base64 Encoding X.509 (.CER)` format, name the export cert to something like `ca.pem`.  
  - Extract Client cert and Client cert private key from `.pfx` file. You need `OpenSSL` to do this, you either install `OpenSSL for Windows` or just use `Git Bash`, since `Git Bash` has `OpenSSL` baked in.
```
Inside Git Bash:    
  Extract client-cert.pem
  openssl pkcs12 -in clientcert.pfx -passin pass:<YOURCERTPASSWORD> -nokeys -clcerts -out client-cert.pem
      
  Extract client-cert-key.pem, this will get password protected
  openssl pkcs12 -in clientcert.pfx -passin pass:<YOURCERTPASSWORD> -nocerts -out client-cert-key.pem -passout pass:<YOURCERTPASSWORD> 
```
    
At this point, you should have all required pieces `ca.pem`, `client-cert.pem`, `client-cert-key.pem` and `clientcert.pfx`.

### No-Windows

As I mentioned before, most Linux backgroud application just expect all certificate related files are on disk, and use `OpenSSL` to deal with cert is quiet common on Liunx, so I assume for customer who wants to setup Build/Release agent on Linux already has `ca.pem`, `client-cert.pem` and `client-cert-key.pem` in place. So the only missing piece should be the client cert archive `.pfx` file.  
```
From Terminal:
openssl pkcs12 -export -out client-cert-archive.pfx -passout pass:<YOURCERTPASSWORD> -inkey client-cert-key.pem -in client-cert.pem -passin pass:<YOURCERTPASSWORD> -certfile CA.pem
```

## Configuration

Pass `--sslcacert`, `--sslclientcert`, `--sslclientcertkey`. `--sslclientcertarchive` and `--sslclientcertpassword` during agent configuration.   
Ex:
```batch
.\config.cmd --sslcacert .\enterprise.pem --sslclientcert .\client.pem --sslclientcertkey .\clientcert-key-pass.pem --sslclientcertarchive .\clientcert-2.pfx --sslclientcertpassword "test123"
```  

We store your client cert private key password securely on each platform.  
Ex:
```
Windows: Windows Credential Store
OSX: OSX Keychain
Linux: Encrypted with symmetric key based on machine id
```

## How agent handle client cert within a Build/Release job

After configuring client cert for agent, agent infrastructure will start talk to VSTS/TFS service using the client cert configured.  

Since the code for `Get Source` step in build job and `Download Artifact` step in release job are also bake into agent, those steps will also follow the agent client cert configuration.  

Agent will expose client cert configuration via environment variables for every task execution, task author need to use `vsts-task-lib` methods to retrieve back client cert configuration and handle client cert with their task.

## Get client cert configuration by using [VSTS-Task-Lib](https://github.com/Microsoft/vsts-task-lib) method

Please reference [VSTS-Task-Lib doc](https://github.com/Microsoft/vsts-task-lib/blob/master/node/docs/cert.md) for detail

## Progress
 - Agent infrastructure (you can configure and queue a build/release) [DONE]
 - Fetch git repository [DONE]
 - Fetch tfvc repository [Only supported in Windows agent]
 - Expose client cert info to task sdk [DONE]
