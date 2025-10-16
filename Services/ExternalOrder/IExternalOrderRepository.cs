

using LuoliCommon.DTO.ExternalOrder;
using LuoliCommon.Entities;

namespace GatewayService.Services.ExternalOrder
{

    public interface IExternalOrderRepository
    {
        Task<ApiResponse<ExternalOrderDTO>> Get(string from_platform, string tid);
        Task<ApiResponse<ExternalOrderDTO>> Get(string coupon);
        Task<ApiResponse<bool>> Update(ExternalOrderDTO dto);
        Task<ApiResponse<bool>> Delete(ExternalOrderDTO dto);
        Task<ApiResponse<bool>> Insert(ExternalOrderDTO dto);

    }
}
