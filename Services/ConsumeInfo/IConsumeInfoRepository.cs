using LuoliCommon.DTO.ConsumeInfo;
using LuoliCommon.DTO.Coupon;
using LuoliCommon.DTO.ExternalOrder;
using LuoliCommon.Entities;

namespace GatewayService.Services.ConsumeInfo
{
    public interface IConsumeInfoRepository
    {
        Task<ApiResponse<ConsumeInfoDTO>> ConsumeInfoQuery(string goodsType, long id);
        Task<ApiResponse<ConsumeInfoDTO>> ConsumeInfoQuery(string goodsType, string coupon);
        Task<ApiResponse<bool>> ConsumeInfoUpdate(LuoliCommon.DTO.ConsumeInfo.UpdateRequest ur);
        Task<ApiResponse<bool>> ConsumeInfoDelete(string goodsType, long id);
        Task<ApiResponse<bool>> ConsumeInfoInsert(ConsumeInfoDTO dto);

    }
}
