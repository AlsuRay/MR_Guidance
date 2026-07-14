#include "pch-cpp.hpp"

#ifndef _MSC_VER
# include <alloca.h>
#else
# include <malloc.h>
#endif



#include "vm/CachedCCWBase.h"
#include "utils/New.h"


// System.Int32[]
struct Int32U5BU5D_t19C97395396A72ECAF310612F0760F165060314C;
// Unity.Sentis.BLASPlugin
struct BLASPlugin_tCB04E3D340765C78866495B0D33CCDC6579C1A35;
// Unity.Sentis.FencedMemoryAlloc
struct FencedMemoryAlloc_tBE621BE2FE5AF698CF2A4AA363EA63D03B62A6D6;
// Unity.Sentis.IBackend
struct IBackend_t4C4AE8D4023C49E545CDCD5F037E21425F3DBB12;
// Unity.Sentis.ITensorAllocator
struct ITensorAllocator_t8B3ED61FD7FE692F59C841A1CE2447C55EE5F203;
// System.Threading.Thread
struct Thread_t0A773B9DE873D2DCAA7D229EAB36757B500E207F;



IL2CPP_EXTERN_C_BEGIN
IL2CPP_EXTERN_C_END

#ifdef __clang__
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Winvalid-offsetof"
#pragma clang diagnostic ignored "-Wunused-variable"
#endif
// Windows.Foundation.IClosable
struct NOVTABLE IClosable_t408735E2F18F562F8A87A4C359E73D7C30A1D301 : Il2CppIInspectable
{
	static const Il2CppGuid IID;
	virtual il2cpp_hresult_t STDCALL IClosable_Close_mCB0DF137CDDDCC22063CF8D95ECE3BC9B8FA0D88() = 0;
};

// Unity.Sentis.CPUBackend
struct CPUBackend_tEF7E2518F5600F459E4291F90957D75ACC99D2DB  : public RuntimeObject
{
	// System.Boolean Unity.Sentis.CPUBackend::m_OwnAllocator
	bool ___m_OwnAllocator_8;
	// Unity.Sentis.ITensorAllocator Unity.Sentis.CPUBackend::m_Allocator
	RuntimeObject* ___m_Allocator_9;
};

// Unity.Sentis.Ops
struct Ops_t8C25DF781CAE0EEE8AF2DCA55A648BAF9FF7BFBF  : public RuntimeObject
{
	// Unity.Sentis.ITensorAllocator Unity.Sentis.Ops::m_Allocator
	RuntimeObject* ___m_Allocator_0;
	// Unity.Sentis.IBackend Unity.Sentis.Ops::m_Backend
	RuntimeObject* ___m_Backend_1;
	// Unity.Sentis.BackendType Unity.Sentis.Ops::m_BackendType
	int32_t ___m_BackendType_2;
};

// Unity.Sentis.GPUCommandBufferOps
struct GPUCommandBufferOps_tA5B735417661FC68DB0D634D6F9F16A021645CC6  : public Ops_t8C25DF781CAE0EEE8AF2DCA55A648BAF9FF7BFBF
{
};

// Unity.Sentis.GPUComputeBackend
struct GPUComputeBackend_t2D5CFBFC59262A9E74A7C39B0283D7305815E9E4  : public CPUBackend_tEF7E2518F5600F459E4291F90957D75ACC99D2DB
{
};

// Unity.Sentis.GPUCommandBufferOps

// Unity.Sentis.GPUCommandBufferOps

// Unity.Sentis.GPUComputeBackend

// Unity.Sentis.GPUComputeBackend
#ifdef __clang__
#pragma clang diagnostic pop
#endif

il2cpp_hresult_t IClosable_Close_mCB0DF137CDDDCC22063CF8D95ECE3BC9B8FA0D88_ComCallableWrapperProjectedMethod(RuntimeObject* __this);



// COM Callable Wrapper for Unity.Sentis.GPUCommandBufferOps
struct GPUCommandBufferOps_tA5B735417661FC68DB0D634D6F9F16A021645CC6_ComCallableWrapper IL2CPP_FINAL : il2cpp::vm::CachedCCWBase<GPUCommandBufferOps_tA5B735417661FC68DB0D634D6F9F16A021645CC6_ComCallableWrapper>, IClosable_t408735E2F18F562F8A87A4C359E73D7C30A1D301
{
	inline GPUCommandBufferOps_tA5B735417661FC68DB0D634D6F9F16A021645CC6_ComCallableWrapper(RuntimeObject* obj) : il2cpp::vm::CachedCCWBase<GPUCommandBufferOps_tA5B735417661FC68DB0D634D6F9F16A021645CC6_ComCallableWrapper>(obj) {}

	virtual il2cpp_hresult_t STDCALL QueryInterface(const Il2CppGuid& iid, void** object) IL2CPP_OVERRIDE
	{
		if (::memcmp(&iid, &Il2CppIUnknown::IID, sizeof(Il2CppGuid)) == 0
		 || ::memcmp(&iid, &Il2CppIInspectable::IID, sizeof(Il2CppGuid)) == 0
		 || ::memcmp(&iid, &Il2CppIAgileObject::IID, sizeof(Il2CppGuid)) == 0)
		{
			*object = GetIdentity();
			AddRefImpl();
			return IL2CPP_S_OK;
		}

		if (::memcmp(&iid, &Il2CppIManagedObjectHolder::IID, sizeof(Il2CppGuid)) == 0)
		{
			*object = static_cast<Il2CppIManagedObjectHolder*>(this);
			AddRefImpl();
			return IL2CPP_S_OK;
		}

		if (::memcmp(&iid, &IClosable_t408735E2F18F562F8A87A4C359E73D7C30A1D301::IID, sizeof(Il2CppGuid)) == 0)
		{
			*object = static_cast<IClosable_t408735E2F18F562F8A87A4C359E73D7C30A1D301*>(this);
			AddRefImpl();
			return IL2CPP_S_OK;
		}

		if (::memcmp(&iid, &Il2CppIMarshal::IID, sizeof(Il2CppGuid)) == 0)
		{
			*object = static_cast<Il2CppIMarshal*>(this);
			AddRefImpl();
			return IL2CPP_S_OK;
		}

		if (::memcmp(&iid, &Il2CppIWeakReferenceSource::IID, sizeof(Il2CppGuid)) == 0)
		{
			*object = static_cast<Il2CppIWeakReferenceSource*>(this);
			AddRefImpl();
			return IL2CPP_S_OK;
		}

		*object = NULL;
		return IL2CPP_E_NOINTERFACE;
	}

	virtual uint32_t STDCALL AddRef() IL2CPP_OVERRIDE
	{
		return AddRefImpl();
	}

	virtual uint32_t STDCALL Release() IL2CPP_OVERRIDE
	{
		return ReleaseImpl();
	}

	virtual il2cpp_hresult_t STDCALL GetIids(uint32_t* iidCount, Il2CppGuid** iids) IL2CPP_OVERRIDE
	{
		Il2CppGuid* interfaceIds = il2cpp_codegen_marshal_allocate_array<Il2CppGuid>(1);
		interfaceIds[0] = IClosable_t408735E2F18F562F8A87A4C359E73D7C30A1D301::IID;

		*iidCount = 1;
		*iids = interfaceIds;
		return IL2CPP_S_OK;
	}

	virtual il2cpp_hresult_t STDCALL GetRuntimeClassName(Il2CppHString* className) IL2CPP_OVERRIDE
	{
		return GetRuntimeClassNameImpl(className);
	}

	virtual il2cpp_hresult_t STDCALL GetTrustLevel(int32_t* trustLevel) IL2CPP_OVERRIDE
	{
		return ComObjectBase::GetTrustLevel(trustLevel);
	}

	virtual il2cpp_hresult_t STDCALL IClosable_Close_mCB0DF137CDDDCC22063CF8D95ECE3BC9B8FA0D88() IL2CPP_OVERRIDE
	{
		return IClosable_Close_mCB0DF137CDDDCC22063CF8D95ECE3BC9B8FA0D88_ComCallableWrapperProjectedMethod(GetManagedObjectInline());
	}
};

IL2CPP_EXTERN_C Il2CppIUnknown* CreateComCallableWrapperFor_GPUCommandBufferOps_tA5B735417661FC68DB0D634D6F9F16A021645CC6(RuntimeObject* obj)
{
	void* memory = il2cpp::utils::Memory::Malloc(sizeof(GPUCommandBufferOps_tA5B735417661FC68DB0D634D6F9F16A021645CC6_ComCallableWrapper));
	if (memory == NULL)
	{
		il2cpp_codegen_raise_out_of_memory_exception();
	}

	return static_cast<Il2CppIManagedObjectHolder*>(new(memory) GPUCommandBufferOps_tA5B735417661FC68DB0D634D6F9F16A021645CC6_ComCallableWrapper(obj));
}

// COM Callable Wrapper for Unity.Sentis.GPUComputeBackend
struct GPUComputeBackend_t2D5CFBFC59262A9E74A7C39B0283D7305815E9E4_ComCallableWrapper IL2CPP_FINAL : il2cpp::vm::CachedCCWBase<GPUComputeBackend_t2D5CFBFC59262A9E74A7C39B0283D7305815E9E4_ComCallableWrapper>, IClosable_t408735E2F18F562F8A87A4C359E73D7C30A1D301
{
	inline GPUComputeBackend_t2D5CFBFC59262A9E74A7C39B0283D7305815E9E4_ComCallableWrapper(RuntimeObject* obj) : il2cpp::vm::CachedCCWBase<GPUComputeBackend_t2D5CFBFC59262A9E74A7C39B0283D7305815E9E4_ComCallableWrapper>(obj) {}

	virtual il2cpp_hresult_t STDCALL QueryInterface(const Il2CppGuid& iid, void** object) IL2CPP_OVERRIDE
	{
		if (::memcmp(&iid, &Il2CppIUnknown::IID, sizeof(Il2CppGuid)) == 0
		 || ::memcmp(&iid, &Il2CppIInspectable::IID, sizeof(Il2CppGuid)) == 0
		 || ::memcmp(&iid, &Il2CppIAgileObject::IID, sizeof(Il2CppGuid)) == 0)
		{
			*object = GetIdentity();
			AddRefImpl();
			return IL2CPP_S_OK;
		}

		if (::memcmp(&iid, &Il2CppIManagedObjectHolder::IID, sizeof(Il2CppGuid)) == 0)
		{
			*object = static_cast<Il2CppIManagedObjectHolder*>(this);
			AddRefImpl();
			return IL2CPP_S_OK;
		}

		if (::memcmp(&iid, &IClosable_t408735E2F18F562F8A87A4C359E73D7C30A1D301::IID, sizeof(Il2CppGuid)) == 0)
		{
			*object = static_cast<IClosable_t408735E2F18F562F8A87A4C359E73D7C30A1D301*>(this);
			AddRefImpl();
			return IL2CPP_S_OK;
		}

		if (::memcmp(&iid, &Il2CppIMarshal::IID, sizeof(Il2CppGuid)) == 0)
		{
			*object = static_cast<Il2CppIMarshal*>(this);
			AddRefImpl();
			return IL2CPP_S_OK;
		}

		if (::memcmp(&iid, &Il2CppIWeakReferenceSource::IID, sizeof(Il2CppGuid)) == 0)
		{
			*object = static_cast<Il2CppIWeakReferenceSource*>(this);
			AddRefImpl();
			return IL2CPP_S_OK;
		}

		*object = NULL;
		return IL2CPP_E_NOINTERFACE;
	}

	virtual uint32_t STDCALL AddRef() IL2CPP_OVERRIDE
	{
		return AddRefImpl();
	}

	virtual uint32_t STDCALL Release() IL2CPP_OVERRIDE
	{
		return ReleaseImpl();
	}

	virtual il2cpp_hresult_t STDCALL GetIids(uint32_t* iidCount, Il2CppGuid** iids) IL2CPP_OVERRIDE
	{
		Il2CppGuid* interfaceIds = il2cpp_codegen_marshal_allocate_array<Il2CppGuid>(1);
		interfaceIds[0] = IClosable_t408735E2F18F562F8A87A4C359E73D7C30A1D301::IID;

		*iidCount = 1;
		*iids = interfaceIds;
		return IL2CPP_S_OK;
	}

	virtual il2cpp_hresult_t STDCALL GetRuntimeClassName(Il2CppHString* className) IL2CPP_OVERRIDE
	{
		return GetRuntimeClassNameImpl(className);
	}

	virtual il2cpp_hresult_t STDCALL GetTrustLevel(int32_t* trustLevel) IL2CPP_OVERRIDE
	{
		return ComObjectBase::GetTrustLevel(trustLevel);
	}

	virtual il2cpp_hresult_t STDCALL IClosable_Close_mCB0DF137CDDDCC22063CF8D95ECE3BC9B8FA0D88() IL2CPP_OVERRIDE
	{
		return IClosable_Close_mCB0DF137CDDDCC22063CF8D95ECE3BC9B8FA0D88_ComCallableWrapperProjectedMethod(GetManagedObjectInline());
	}
};

IL2CPP_EXTERN_C Il2CppIUnknown* CreateComCallableWrapperFor_GPUComputeBackend_t2D5CFBFC59262A9E74A7C39B0283D7305815E9E4(RuntimeObject* obj)
{
	void* memory = il2cpp::utils::Memory::Malloc(sizeof(GPUComputeBackend_t2D5CFBFC59262A9E74A7C39B0283D7305815E9E4_ComCallableWrapper));
	if (memory == NULL)
	{
		il2cpp_codegen_raise_out_of_memory_exception();
	}

	return static_cast<Il2CppIManagedObjectHolder*>(new(memory) GPUComputeBackend_t2D5CFBFC59262A9E74A7C39B0283D7305815E9E4_ComCallableWrapper(obj));
}
