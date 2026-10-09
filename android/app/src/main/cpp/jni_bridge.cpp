#include <jni.h>
#include "audio_engine.h"
#include <memory>
#include <string>

static std::unique_ptr<audiobridge::AudioEngine> sEngine;

extern "C" {

JNIEXPORT void JNICALL
Java_com_audiobridge_NativeEngine_nativeInit(JNIEnv* /*env*/, jobject /*thiz*/) {
    if (!sEngine) {
        sEngine = std::make_unique<audiobridge::AudioEngine>();
    }
}

JNIEXPORT jboolean JNICALL
Java_com_audiobridge_NativeEngine_nativeStart(
        JNIEnv* env, jobject /*thiz*/,
        jstring hostStr, jint port, jint mode, jstring nameStr, jfloat targetMs) {
    if (!sEngine) sEngine = std::make_unique<audiobridge::AudioEngine>();

    const char* host = env->GetStringUTFChars(hostStr, nullptr);
    const char* name = env->GetStringUTFChars(nameStr, nullptr);

    bool ok = sEngine->start(
        std::string(host),
        port,
        static_cast<audiobridge::NetworkMode>(mode),
        std::string(name),
        targetMs
    );

    env->ReleaseStringUTFChars(hostStr, host);
    env->ReleaseStringUTFChars(nameStr, name);
    return ok ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_com_audiobridge_NativeEngine_nativeStop(JNIEnv* /*env*/, jobject /*thiz*/) {
    if (sEngine) {
        sEngine->stop();
    }
}

JNIEXPORT void JNICALL
Java_com_audiobridge_NativeEngine_nativeSetTargetMs(JNIEnv* /*env*/, jobject /*thiz*/, jfloat targetMs) {
    if (sEngine) {
        sEngine->setTargetMs(targetMs);
    }
}

JNIEXPORT void JNICALL
Java_com_audiobridge_NativeEngine_nativeGetStats(
        JNIEnv* env, jobject /*thiz*/,
        jfloatArray floatArray, jintArray intArray) {
    if (!sEngine) return;

    audiobridge::ReceiverStats st = sEngine->getStats();

    jfloat fData[5] = {
        st.bufferMs,
        st.targetMs,
        st.outputLatencyMs,
        st.jitterMs,
        st.driftPpm
    };
    env->SetFloatArrayRegion(floatArray, 0, 5, fData);

    jint iData[6] = {
        static_cast<jint>(st.packetsLost),
        static_cast<jint>(st.packetsLate),
        static_cast<jint>(st.underruns),
        static_cast<jint>(st.packetsReceived),
        static_cast<jint>(st.sampleRate),
        static_cast<jint>(st.channels)
    };
    env->SetIntArrayRegion(intArray, 0, 6, iData);
}

JNIEXPORT jboolean JNICALL
Java_com_audiobridge_NativeEngine_nativeSelfTest(JNIEnv* /*env*/, jobject /*thiz*/) {
    // Basic verification test of jitter buffer and resampler
    audiobridge::JitterBuffer jb;
    std::vector<int16_t> pcm(480, 1000); // 240 stereo frames
    jb.pushPacket(0, pcm.data(), 240, 48000);

    std::vector<float> out(480, 0.0f);
    jb.pullFrames(out.data(), 240);

    if (jb.getUnderruns() != 0) return JNI_FALSE;
    if (out[0] < 0.01f) return JNI_FALSE; // Audio successfully transferred

    audiobridge::DriftResampler resampler;
    resampler.reset();
    resampler.setTargetMs(15.0f);

    return JNI_TRUE;
}

} // extern "C"
