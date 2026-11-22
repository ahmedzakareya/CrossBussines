plugins {
    id("com.android.application")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

android {
    namespace = "com.example.crossbuy_mobile"
    compileSdk = flutter.compileSdkVersion
    ndkVersion = flutter.ndkVersion

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
        // flutter_local_notifications (push, 2026-11-22) uses java.time on older Android: desugared
        isCoreLibraryDesugaringEnabled = true
    }

    defaultConfig {
        // TODO: Specify your own unique Application ID (https://developer.android.com/studio/build/application-id.html).
        applicationId = "com.example.crossbuy_mobile"
        // You can update the following values to match your application needs.
        // For more information, see: https://flutter.dev/to/review-gradle-config.
        // MediaPipe tasks-vision (background blur in calls) needs Android 7.0 (API 24) or newer
        minSdk = maxOf(flutter.minSdkVersion, 24)
        targetSdk = flutter.targetSdkVersion
        versionCode = flutter.versionCode
        versionName = flutter.versionName
    }

    // the selfie model is read straight from the APK: it must not be compressed
    androidResources {
        noCompress += "tflite"
    }

    buildTypes {
        release {
            // TODO: Add your own signing config for the release build.
            // Signing with the debug keys for now, so `flutter run --release` works.
            signingConfig = signingConfigs.getByName("debug")
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

flutter {
    source = "../.."
}

dependencies {
    // Background blur in calls (owner 2026-11-19). The same WebRTC build flutter_webrtc 1.6.3 uses (it declares it
    // `implementation`, so the app names it to compile BlurProcessor against org.webrtc), and MediaPipe's vision tasks
    // (Apache-2.0; 1.2.2, published 2026-11-03 — the browser uses the same version and the same selfie model).
    implementation("io.github.webrtc-sdk:android:150.7871.04")
    implementation("com.google.mediapipe:tasks-vision:1.2.2")
    // what flutter_local_notifications requires for the desugaring above
    coreLibraryDesugaring("com.android.tools:desugar_jdk_libs:2.1.4")
}
