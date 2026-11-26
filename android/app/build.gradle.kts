import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Release signing comes from a properties file outside the repo (path in local.properties: n8.signing=...).
val signing = Properties().also { props ->
    val local = rootProject.file("local.properties")
    if (local.exists()) {
        val path = Properties().apply { local.inputStream().use { load(it) } }.getProperty("n8.signing")
        if (path != null && file(path).exists()) file(path).inputStream().use { props.load(it) }
    }
}

android {
    namespace = "com.n8.glucose"
    compileSdk = 35
    defaultConfig {
        applicationId = "com.n8.glucose"
        minSdk = 26
        targetSdk = 35
        versionCode = 2
        versionName = "1.0.1"
    }
    signingConfigs {
        if (signing.getProperty("storeFile") != null) create("release") {
            storeFile = file(signing.getProperty("storeFile"))
            storePassword = signing.getProperty("storePassword")
            keyAlias = signing.getProperty("keyAlias")
            keyPassword = signing.getProperty("keyPassword")
        }
    }
    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.findByName("release")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies {
    testImplementation("junit:junit:4.13.2")
    // Android's org.json is a stub in local unit tests; use the real one there.
    testImplementation("org.json:json:20240303")
}
