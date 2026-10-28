plugins { id("com.android.application"); id("org.jetbrains.kotlin.android") }
android {
    namespace = "cn.obstacletimer.finish"
    compileSdk = 35
    defaultConfig { applicationId = "cn.obstacletimer.finish"; minSdk = 29; targetSdk = 35; versionCode = 1; versionName = "0.1.0" }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget = "17" }
    buildTypes { getByName("release") { isMinifyEnabled = false } }
}
dependencies {
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("com.google.zxing:core:3.5.3")
}
